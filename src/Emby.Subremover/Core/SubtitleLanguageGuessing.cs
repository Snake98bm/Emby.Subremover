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

    public static class SubtitleLanguageGuessing
    {
        /// <summary>How much of the video is read: enough dialogue to tell the language, without reading the whole file.</summary>
        public const int SecondsRead = 900;

        /// <summary>
        /// Text tracks worth reading: those without a language, and those sharing a language with another one, where a
        /// wrong tag (e.g. 24 "English" tracks in different languages) or an untitled SDH copy is common.
        /// </summary>
        public static List<ProbeStream> TracksToRead(ProbeResult probe)
        {
            var text = probe.streams.Where(s => s.IsSubtitle && SubtitlePlanner.IsTextCodec(s.codec_name)).ToList();
            var duplicates = text.Where(s => s.Language != "und" && !s.IsForced && !s.IsCommentary)
                .GroupBy(s => s.Language, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g);
            return text.Where(s => s.Language == "und").Concat(duplicates).OrderBy(s => s.index).ToList();
        }

        /// <summary>
        /// Reads the start of those tracks (one ffmpeg pass for all of them). A track without a language gets the guessed
        /// one; a tagged track only when its text is clearly another language. With <paramref name="filesOnly"/> the track
        /// keeps its tag and the guess only names its extracted file. Text that reads like SDH marks the track for an .sdh file.
        /// </summary>
        public static async Task<List<TrackReading>> Run(FfmpegRunner runner, string videoPath, ProbeResult probe,
            CancellationToken ct, bool filesOnly = false)
        {
            var results = new List<TrackReading>();
            var tracks = TracksToRead(probe);
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
                    results.Add(Apply(tracks[i], text, result.Success ? null : $"ffmpeg couldn't read it (exit {result.ExitCode})", filesOnly));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return results;
        }

        /// <summary>What the text of one track says, applied to the track.</summary>
        public static TrackReading Apply(ProbeStream stream, string text, string readProblem, bool filesOnly)
        {
            var reading = new TrackReading
            {
                Stream = stream,
                Tag = stream.Language,
                Guess = readProblem != null ? new LanguageGuess { Reason = readProblem } : LanguageGuesser.Guess(text)
            };
            if (readProblem == null && !stream.IsHearingImpaired && SdhDetector.Looks(text, out var sdhReason))
            {
                stream.LooksHearingImpaired = true;
                reading.Guess.SdhReason = sdhReason;
            }

            var language = reading.Guess.Language;
            if (language == null)
            {
                // Can't say what it is, but clearly not what the tag says: it's treated as having no language.
                if (reading.Tag == "und" || !LanguageGuesser.RulesOutTag(reading.Guess, reading.Tag)) return reading;
                language = "und";
                reading.WrongTag = true;
            }
            else
            {
                reading.WrongTag = reading.Tag != "und" && LanguageGuesser.ContradictsTag(reading.Guess, reading.Tag);
                if (reading.Tag != "und" && !reading.WrongTag) return reading;
            }

            if (filesOnly) stream.GuessedLanguage = language;
            else SetGuessedLanguage(stream, language);
            return reading;
        }

        public static void SetGuessedLanguage(ProbeStream stream, string language)
        {
            var tags = stream.tags ?? new Dictionary<string, string>();
            foreach (var key in tags.Keys.Where(k => string.Equals(k, "language", StringComparison.OrdinalIgnoreCase)).ToList()) tags.Remove(key);
            tags["language"] = language;
            stream.tags = tags;
            stream.LanguageGuessed = true;
            stream.GuessedLanguage = language;
        }
    }
}
