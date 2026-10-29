using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Subremover.Core
{
    public class PlannerSettings
    {
        public ISet<string> KeepLanguages { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool KeepUndefined { get; set; } = true;
        public bool KeepForcedAnyLanguage { get; set; }
        public bool RemoveHearingImpaired { get; set; }
        public bool RemoveCommentary { get; set; }
        public bool ExtractTextSubtitles { get; set; }
        public bool RemoveExtractedFromVideo { get; set; }
        public bool OcrPictureSubtitles { get; set; }
        public bool OcrReplaceExisting { get; set; }
        public bool MakeRegularDefault { get; set; }
        public bool MarkDefaultInName { get; set; }

        /// <summary>Language whose regular subtitle gets the default, when it's kept. Null: the video's own default decides.</summary>
        public string DefaultLanguage { get; set; }

        /// <summary>Only part of the signature: the guessing itself happens before planning, on the probe.</summary>
        public bool GuessLanguages { get; set; }
        public bool GuessForFilesOnly { get; set; }
        public bool PreferExistingFiles { get; set; }

        public static ISet<string> ParseLanguages(string csv) =>
            new HashSet<string>(
                (csv ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Languages.Normalize),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>Changes whenever a setting that affects the plan changes, so already-checked videos get checked again.</summary>
        public string Signature() => string.Join("|",
            string.Join(",", KeepLanguages.OrderBy(l => l, StringComparer.OrdinalIgnoreCase)),
            KeepUndefined, KeepForcedAnyLanguage, RemoveHearingImpaired, RemoveCommentary, ExtractTextSubtitles, RemoveExtractedFromVideo, OcrPictureSubtitles, OcrReplaceExisting, MakeRegularDefault, MarkDefaultInName, DefaultLanguage, GuessLanguages, GuessForFilesOnly, PreferExistingFiles);
    }

    public class ExtractJob
    {
        public ProbeStream Stream { get; set; }
        public string OutputPath { get; set; }
        public bool ConvertToSrt { get; set; }
    }

    public class OcrJob
    {
        public ProbeStream Stream { get; set; }
        public string OutputPath { get; set; }
        public string TesseractLanguage { get; set; }
    }

    public class SubtitlePlan
    {
        public List<ProbeStream> Keep { get; } = new List<ProbeStream>();
        public List<ProbeStream> Remove { get; } = new List<ProbeStream>();
        public List<ExtractJob> Extract { get; } = new List<ExtractJob>();
        public List<OcrJob> Ocr { get; } = new List<OcrJob>();

        /// <summary>Kept track that gets the "default" flag; every other kept subtitle loses it. Null leaves the flags alone.</summary>
        public ProbeStream NewDefault { get; set; }

        /// <summary>False for containers that can't store a subtitle language (MPEG-TS, AVI).</summary>
        public bool CanWriteLanguages { get; set; } = true;

        /// <summary>Languages left to a subtitle file that was already there (language -> file name).</summary>
        public IDictionary<string, string> LeftToExistingFiles { get; set; } = new Dictionary<string, string>();

        /// <summary>Kept tracks whose "default" flag comes off so Emby turns the existing file on instead (when NewDefault is null).</summary>
        public List<ProbeStream> ClearDefault { get; } = new List<ProbeStream>();

        public bool NeedsRemux => Remove.Count > 0 || NewDefault != null || ClearDefault.Count > 0 || LanguageChanges().Count > 0;

        /// <summary>Guessed languages to write into kept tracks, by input stream index.</summary>
        public IDictionary<int, string> LanguageChanges() =>
            CanWriteLanguages
                ? Keep.Where(s => s.LanguageGuessed).ToDictionary(s => s.index, s => s.Language)
                : new Dictionary<int, string>();
        public bool HasWork => NeedsRemux || Extract.Count > 0 || Ocr.Count > 0;

        /// <summary>Default flags to change, by input stream index: true sets it, false clears it.</summary>
        public IDictionary<int, bool> DefaultChanges()
        {
            var changes = new Dictionary<int, bool>();
            if (NewDefault == null)
            {
                foreach (var s in ClearDefault) changes[s.index] = false;
                return changes;
            }
            foreach (var s in Keep)
            {
                var isDefault = s.Disposition("default");
                if (s == NewDefault && !isDefault) changes[s.index] = true;
                else if (s != NewDefault && isDefault) changes[s.index] = false;
            }
            return changes;
        }
    }

    public static class SubtitlePlanner
    {
        private static readonly HashSet<string> TextCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer", "subviewer1",
            "realtext", "sami", "stl", "jacosub", "mpl2", "pjs", "vplayer", "ttml"
        };

        private static readonly HashSet<string> AssCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ass", "ssa" };

        // Marker put in OCR'd file names so it's obvious the text came from a picture track.
        private static readonly Dictionary<string, string> PictureCodecMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "hdmv_pgs_subtitle", "pgs" },
            { "dvd_subtitle", "dvd" },
            { "dvb_subtitle", "dvb" },
            { "xsub", "xsub" },
        };

        public static bool IsTextCodec(string codec) => codec != null && TextCodecs.Contains(codec);

        public static bool IsPictureCodec(string codec) => codec != null && PictureCodecMarkers.ContainsKey(codec);

        /// <summary>The OCR file of a picture track, as a run would name it ("Movie.pgs.eng.srt").</summary>
        internal static string OcrFilePath(string videoPath, ProbeStream s) =>
            SidecarPath(videoPath, s, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "." + PictureCodecMarkers[s.codec_name], ".srt");

        /// <param name="existingFiles">Subtitle files already next to the video that SubRemover didn't make (language -> file name).</param>
        public static SubtitlePlan Plan(ProbeResult probe, PlannerSettings settings, string videoPath, IDictionary<string, string> existingFiles = null)
        {
            var plan = new SubtitlePlan { CanWriteLanguages = FfmpegArgs.CanSetSubtitleFlags(videoPath) };
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var subtitles = probe.streams.Where(x => x.IsSubtitle).ToList();

            // "Prefer subtitle files that are already there": a language with such a file gets no file of ours and no default.
            if (settings.PreferExistingFiles && existingFiles != null)
            {
                foreach (var kv in existingFiles.Where(kv => settings.KeepLanguages.Contains(kv.Key))) plan.LeftToExistingFiles[kv.Key] = kv.Value;
            }
            bool LeftToFile(ProbeStream s) => plan.LeftToExistingFiles.ContainsKey(EffectiveLanguage(s));

            // Languages that keep a regular (not SDH, forced-only or commentary) track. SDH is only removed where one of
            // these stays, so a video whose only English track is SDH doesn't lose its English subtitles.
            // A track whose text reads as another language than its tag doesn't count for its tag: a French track tagged
            // English must not get the only real English (SDH) track removed.
            var regularLanguages = new HashSet<string>(
                subtitles.Where(x => IsRegular(x) && KeptByLanguage(x, settings)).Select(EffectiveLanguage),
                StringComparer.OrdinalIgnoreCase);

            var keptText = subtitles.Where(x => IsTextCodec(x.codec_name) && ShouldKeep(x, settings, regularLanguages)).ToList();

            // One sidecar per video can be named ".default" so Emby turns it on: a track that gets one, in the chosen language,
            // else in the language the video flags as default, else the first in the video. Within that language a regular
            // track wins; SDH gets it when it's the only one.
            // No ".default" at all when the language the default should go to is left to an existing file: that file is the one to turn on.
            var defaultTarget = settings.DefaultLanguage ?? subtitles.Where(f => f.Disposition("default")).Select(EffectiveLanguage).FirstOrDefault();
            ProbeStream defaultSidecar = null;
            if (settings.MarkDefaultInName && (defaultTarget == null || !plan.LeftToExistingFiles.ContainsKey(defaultTarget)))
            {
                var candidates = subtitles.Where(x => CanBeDefault(x) && !LeftToFile(x) && FileLanguage(x, settings) != null && ShouldKeep(x, settings, regularLanguages)
                                                      && (WillOcr(x, settings, keptText) || (settings.ExtractTextSubtitles && IsTextCodec(x.codec_name))))
                    .ToList();
                defaultSidecar = Best(candidates.Where(x => SameLanguage(FileLanguage(x, settings), settings.DefaultLanguage)))
                                 ?? Best(candidates.Where(x => subtitles.Any(f => f.Disposition("default") && SameLanguage(f.Language, FileLanguage(x, settings)))))
                                 ?? Best(candidates);
            }

            foreach (var s in subtitles)
            {
                if (!ShouldKeep(s, settings, regularLanguages))
                {
                    plan.Remove.Add(s);
                    continue;
                }

                if (WillOcr(s, settings, keptText) && !LeftToFile(s))
                {
                    var output = SidecarPath(videoPath, s, usedPaths, "." + PictureCodecMarkers[s.codec_name], ".srt", s == defaultSidecar);
                    if (settings.OcrReplaceExisting || !File.Exists(output))
                    {
                        plan.Ocr.Add(new OcrJob { Stream = s, OutputPath = output, TesseractLanguage = Languages.TesseractCode(s.Language) });
                    }
                }

                // A track without a language stays in the video: as a file it would only show up as "Unknown".
                var fileLanguage = FileLanguage(s, settings);
                if (settings.ExtractTextSubtitles && IsTextCodec(s.codec_name) && fileLanguage != null && !LeftToFile(s))
                {
                    var isAss = AssCodecs.Contains(s.codec_name);
                    plan.Extract.Add(new ExtractJob
                    {
                        Stream = s,
                        OutputPath = SidecarPath(videoPath, s, usedPaths, "", isAss ? ".ass" : ".srt", s == defaultSidecar, fileLanguage),
                        ConvertToSrt = !isAss
                    });

                    // A track that only has a guess for its file stays in the video as it is.
                    if (settings.RemoveExtractedFromVideo && s.Language != "und" && (s.GuessedLanguage == null || s.LanguageGuessed))
                    {
                        plan.Remove.Add(s);
                        continue;
                    }
                }

                plan.Keep.Add(s);
            }

            var canFlag = FfmpegArgs.CanSetSubtitleFlags(videoPath);
            if (settings.MakeRegularDefault && canFlag)
                plan.NewDefault = RegularDefault(subtitles, plan.Keep.Where(x => !LeftToFile(x)).ToList(), settings.DefaultLanguage);

            // Emby prefers an embedded track flagged default over a file, so in a language left to an existing file the
            // embedded flags come off. The tracks themselves stay, as a fallback if that file turns out wrong. This goes by
            // the track's own language: a guess only noted for files ("never change tracks") doesn't change the video.
            if (canFlag && plan.NewDefault == null)
                plan.ClearDefault.AddRange(plan.Keep.Where(x => plan.LeftToExistingFiles.ContainsKey(x.Language) && x.Disposition("default")));
            return plan;
        }

        // Commentary isn't OCR'd: the text file would show up in Emby as just another subtitle in that language. Neither is
        // a picture track with a kept text track in its language (a forced one for forced pictures): that text can be
        // extracted instead, and the OCR copy would be a worse duplicate that Emby turns on because it's a separate file.
        private static bool WillOcr(ProbeStream s, PlannerSettings settings, IList<ProbeStream> keptText) =>
            settings.OcrPictureSubtitles && IsPictureCodec(s.codec_name) && !s.IsCommentary
            && Languages.TesseractCode(s.Language) != null
            && !keptText.Any(t => !t.IsCommentary && t.IsForced == s.IsForced
                                  && string.Equals(FileLanguage(t, settings) ?? t.Language, s.Language, StringComparison.OrdinalIgnoreCase));

        // The language a track's file is named after. A guess that was only noted for files ("guess only for files": the
        // track kept "und" or a tag its text contradicts) names it when it's a language you keep, else the track gets no
        // file. Otherwise the track's own language; none for "und".
        private static string FileLanguage(ProbeStream s, PlannerSettings settings)
        {
            if (s.GuessedLanguage != null && !s.LanguageGuessed)
                return settings.KeepLanguages.Contains(s.GuessedLanguage) ? s.GuessedLanguage : null;
            return s.Language != "und" ? s.Language : null;
        }

        // The language a track really is as far as SubRemover can tell: what its text reads as when that was checked, else
        // its tag. Differs from the tag only for "guess only for files", where the tag itself is left alone.
        private static string EffectiveLanguage(ProbeStream s) => s.GuessedLanguage ?? s.Language;

        private static bool IsRegular(ProbeStream s) => !s.IsHearingImpaired && !s.LooksHearingImpaired && !s.IsForced && !s.IsCommentary;

        // Forced subtitles only show foreign-language lines and commentary isn't the film's dialogue, so neither is a default.
        private static bool CanBeDefault(ProbeStream s) => !s.IsForced && !s.IsCommentary;

        // A regular track before SDH, then track order.
        private static ProbeStream Best(IEnumerable<ProbeStream> tracks) =>
            tracks.OrderBy(x => IsRegular(x) ? 0 : 1).FirstOrDefault();

        private static bool SameLanguage(string a, string b) => b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // Emby turns on the subtitle track the file flags as default. A chosen language overrides the video: its regular
        // track (or its SDH track when that's the only one) becomes the only default, whatever the video flagged (or
        // didn't). Otherwise, or when the video has nothing in that language, the video decides: a flag on an SDH, CC or
        // commentary track moves to the regular track in the same language, a forced default stays (forced subtitles only
        // show foreign-language lines), a flag already on a kept regular track stays there, and a video without a default
        // keeps its subtitles off. Languages are the ones the text reads as, so a French track tagged English never
        // becomes the English default.
        private static ProbeStream RegularDefault(IList<ProbeStream> subtitles, IList<ProbeStream> kept, string defaultLanguage)
        {
            var regular = Best(kept.Where(x => CanBeDefault(x) && SameLanguage(EffectiveLanguage(x), defaultLanguage)));
            if (regular == null)
            {
                var flagged = subtitles.FirstOrDefault(x => x.Disposition("default"));
                if (flagged == null || flagged.IsForced) return null;

                regular = kept.Contains(flagged) && IsRegular(flagged)
                    ? flagged
                    : kept.FirstOrDefault(x => IsRegular(x) && SameLanguage(EffectiveLanguage(x), EffectiveLanguage(flagged)));
                if (regular == null) return null;
            }

            var keptFlagged = kept.Where(x => x.Disposition("default")).ToList();
            return keptFlagged.Count == 1 && keptFlagged[0] == regular ? null : regular;
        }

        private static bool ShouldKeep(ProbeStream s, PlannerSettings settings, ISet<string> regularLanguages)
        {
            if (settings.RemoveHearingImpaired && s.IsHearingImpaired && regularLanguages.Contains(s.Language)) return false;
            return KeptByLanguage(s, settings);
        }

        private static bool KeptByLanguage(ProbeStream s, PlannerSettings settings)
        {
            if (settings.RemoveCommentary && s.IsCommentary) return false;
            if (settings.KeepLanguages.Contains(s.Language)) return true;
            if (s.Language == "und" && settings.KeepUndefined) return true;
            if (s.IsForced && settings.KeepForcedAnyLanguage) return true;
            return false;
        }

        // Emby picks up sidecars named "<video name>[.marker].<lang>[.sdh][.forced][.default].<ext>". A track that only
        // reads like SDH gets ".sdh" too.
        internal static string SidecarPath(string videoPath, ProbeStream s, ISet<string> usedPaths, string marker, string ext, bool markDefault = false,
            string language = null)
        {
            var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(videoPath);
            var suffix = marker + "." + (language ?? s.Language) + (s.IsHearingImpaired || s.LooksHearingImpaired ? ".sdh" : "") + (s.IsForced ? ".forced" : "");
            var wanted = suffix + (markDefault ? ".default" : "");

            // A file written earlier with or without ".default" is this track's file, so turning the option on or off
            // doesn't leave a second copy next to it.
            var path = Path.Combine(dir, name + wanted + ext);
            var otherName = Path.Combine(dir, name + suffix + (markDefault ? "" : ".default") + ext);
            if (!File.Exists(path) && File.Exists(otherName)) path = otherName;

            if (!usedPaths.Add(path))
            {
                path = Path.Combine(dir, name + wanted + ".track" + s.index + ext);
                usedPaths.Add(path);
            }
            return path;
        }
    }
}
