using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Subremover.Core
{
    /// <summary>One subtitle track whose text was read, with what the text says about it.</summary>
    public class TrackReading
    {
        public ProbeStream Stream { get; set; }
        public LanguageGuess Guess { get; set; }

        /// <summary>The language the file gave the track before reading ("und" when none).</summary>
        public string Tag { get; set; }

        /// <summary>The text is clearly in another language than the track's tag says.</summary>
        public bool WrongTag { get; set; }
    }

    /// <summary>What a reading may change: the language-guessing settings.</summary>
    public class GuessRules
    {
        /// <summary>"Guess the language of subtitles with no language set".</summary>
        public bool GuessUntagged { get; set; }

        /// <summary>"...only to name extracted files": a guess for a track with no language never changes the video.</summary>
        public bool UntaggedFilesOnly { get; set; }

        /// <summary>"Subtitles tagged with the wrong language: Fix the tag".</summary>
        public bool FixWrongTags { get; set; }

        /// <summary>"...remove the ones whose real language can't be told".</summary>
        public bool RemoveUnknownWrongTags { get; set; }

        /// <summary>"...check every text subtitle".</summary>
        public bool CheckEveryTextSubtitle { get; set; }

        /// <summary>The rules before 1.0.0, where one switch covered both: files only, or change the tracks.</summary>
        public static GuessRules Classic(bool filesOnly) =>
            new GuessRules { GuessUntagged = true, UntaggedFilesOnly = filesOnly, FixWrongTags = !filesOnly };
    }

    public static class SubtitleLanguageGuessing
    {
        /// <summary>How much of the video is read: enough dialogue to tell the language, without reading the whole file.</summary>
        public const int SecondsRead = 900;

        /// <summary>
        /// Text tracks worth reading: those without a language, and those sharing a language with another one, where a
        /// wrong tag (e.g. 24 "English" tracks in different languages) or an untitled SDH copy is common.
        /// </summary>
        /// <param name="everyTrack">"...check every text subtitle": every tagged track too, not only shared tags.</param>
        public static List<ProbeStream> TracksToRead(ProbeResult probe, bool everyTrack = false)
        {
            var text = probe.streams.Where(s => s.IsSubtitle && SubtitlePlanner.IsTextCodec(s.codec_name)).ToList();
            var tagged = text.Where(s => s.Language != "und" && !s.IsForced && !s.IsCommentary);
            var checkedTagged = everyTrack
                ? tagged
                : tagged.GroupBy(s => s.Language, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g);
            return text.Where(s => s.Language == "und").Concat(checkedTagged).OrderBy(s => s.index).ToList();
        }

        /// <summary>
        /// Reads the start of those tracks (one ffmpeg pass for all of them). A track without a language gets the guessed
        /// one; a tagged track only when its text is clearly another language. With <paramref name="filesOnly"/> the track
        /// keeps its tag and the guess only names its extracted file. Text that reads like SDH marks the track for an .sdh file,
        /// only on tracks <paramref name="mightKeep"/> says may stay (null: every track).
        /// </summary>
        public static Task<List<TrackReading>> Run(FfmpegRunner runner, string videoPath, ProbeResult probe,
            CancellationToken ct, bool filesOnly = false, Func<ProbeStream, bool> mightKeep = null) =>
            Run(runner, videoPath, probe, ct, GuessRules.Classic(filesOnly), mightKeep);

        public static async Task<List<TrackReading>> Run(FfmpegRunner runner, string videoPath, ProbeResult probe,
            CancellationToken ct, GuessRules rules, Func<ProbeStream, bool> mightKeep = null)
        {
            var results = new List<TrackReading>();
            var tracks = TracksToRead(probe, rules.CheckEveryTextSubtitle);
            if (tracks.Count == 0) return results;

            var dir = Path.Combine(Path.GetTempPath(), "subremover-guess-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var outputs = tracks.Select(t => Path.Combine(dir, t.index + ".srt")).ToList();
                var result = await runner.RunFfmpeg(FfmpegArgs.ReadSubtitleText(videoPath, tracks.Select(t => t.index).ToList(), outputs, SecondsRead), ct)
                    .ConfigureAwait(false);

                for (var i = 0; i < tracks.Count; i++)
                {
                    var text = result.Success && File.Exists(outputs[i]) ? File.ReadAllText(outputs[i]) : string.Empty;
                    results.Add(Apply(tracks[i], text, result.Success ? null : $"ffmpeg couldn't read it (exit {result.ExitCode})", rules, mightKeep));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return results;
        }

        /// <summary>What the text of one track says, applied to the track.</summary>
        public static TrackReading Apply(ProbeStream stream, string text, string readProblem, bool filesOnly,
            Func<ProbeStream, bool> mightKeep = null) =>
            Apply(stream, text, readProblem, GuessRules.Classic(filesOnly), mightKeep);

        public static TrackReading Apply(ProbeStream stream, string text, string readProblem, GuessRules rules,
            Func<ProbeStream, bool> mightKeep = null)
        {
            var reading = new TrackReading
            {
                Stream = stream,
                Tag = stream.Language,
                Guess = readProblem != null ? new LanguageGuess { Reason = readProblem } : LanguageGuesser.Guess(text)
            };
            ApplyLanguage(reading, rules);

            // After the language, so an "English" track that reads as Spanish counts as the Spanish it is.
            if (readProblem == null && !stream.IsHearingImpaired && (mightKeep == null || mightKeep(stream))
                && SdhDetector.Looks(text, out var sdhReason))
            {
                stream.LooksHearingImpaired = true;
                reading.Guess.SdhReason = sdhReason;
            }
            return reading;
        }

        private static void ApplyLanguage(TrackReading reading, GuessRules rules)
        {
            var stream = reading.Stream;
            var language = reading.Guess.Language;

            // No language set: "Guess the language of subtitles with no language set".
            if (reading.Tag == "und")
            {
                if (!rules.GuessUntagged || language == null) return;
                if (rules.UntaggedFilesOnly) stream.GuessedLanguage = language;
                else SetGuessedLanguage(stream, language);
                return;
            }

            // Tagged: "Subtitles tagged with the wrong language".
            if (language == null)
            {
                // Can't say what it is, but clearly not what the tag says.
                if (!LanguageGuesser.RulesOutTag(reading.Guess, reading.Tag)) return;
                language = "und";
            }
            else if (!LanguageGuesser.ContradictsTag(reading.Guess, reading.Tag)) return;
            reading.WrongTag = true;

            if (!rules.FixWrongTags)
            {
                // Left as tagged; the reading only names its extracted file (none when it can't be told).
                stream.GuessedLanguage = language;
                return;
            }
            SetGuessedLanguage(stream, language);
            if (language == "und" && rules.RemoveUnknownWrongTags) stream.RemoveAsWrongTag = true;
        }

        public static void SetGuessedLanguage(ProbeStream stream, string language)
        {
            if (stream.TagBeforeGuess == null) stream.TagBeforeGuess = stream.Language;
            var tags = stream.tags ?? new Dictionary<string, string>();
            foreach (var key in tags.Keys.Where(k => string.Equals(k, "language", StringComparison.OrdinalIgnoreCase)).ToList()) tags.Remove(key);
            tags["language"] = language;
            stream.tags = tags;
            stream.LanguageGuessed = true;
            stream.GuessedLanguage = language;
        }
    }
}
