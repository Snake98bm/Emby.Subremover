using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>A file SubRemover made next to a video in the same step as the backup.</summary>
    public sealed class MadeFile
    {
        public string Path { get; set; }
        public bool Ocr { get; set; }
    }

    /// <summary>
    /// The one or two lines the restore list shows under a video: what SubRemover changed when it made the backup.
    /// "Removed 5 subtitles: French, Spanish (2), German, English commentary" /
    /// "Made .pgs.eng.srt (OCR, both models) · default now English".
    /// </summary>
    public static class BackupNotes
    {
        /// <summary>Two short parts this long or less go on one line.</summary>
        private const int OneLine = 90;

        private const int MaxListed = 6;

        public static List<string> Describe(string videoPath, SubtitlePlan plan, IEnumerable<MadeFile> made, OcrModels model)
        {
            var stem = Path.GetFileNameWithoutExtension(FileName(videoPath));
            var made_ = (made ?? Enumerable.Empty<MadeFile>()).ToList();

            var removed = plan.Remove.Count == 0
                ? null
                : $"Removed {plan.Remove.Count} subtitle{(plan.Remove.Count == 1 ? "" : "s")}: {List(Grouped(plan.Remove.Select(Label)))}";

            var other = new List<string>();
            var extracted = made_.Where(m => !m.Ocr).Select(m => Short(m.Path, stem)).ToList();
            if (extracted.Count > 0) other.Add("extracted " + List(extracted));
            var ocr = made_.Where(m => m.Ocr).Select(m => Short(m.Path, stem)).ToList();
            if (ocr.Count > 0) other.Add($"made {List(ocr)} (OCR, {ModelText(model)})");
            if (plan.NewDefault != null) other.Add("default now " + Label(plan.NewDefault));
            else if (plan.ClearDefault.Count > 0) other.Add(DefaultTurnedOff(plan, stem));
            var languages = plan.LanguageChanges().Count;
            if (languages > 0) other.Add($"language written into {languages} track{(languages == 1 ? "" : "s")}");

            var rest = other.Count == 0 ? null : string.Join(" · ", other);
            var lines = new List<string>();
            if (removed != null && rest != null && removed.Length + rest.Length + 3 <= OneLine) lines.Add(removed + " · " + rest);
            else
            {
                if (removed != null) lines.Add(removed);
                if (rest != null) lines.Add(Capitalize(rest));
            }
            if (lines.Count == 0) lines.Add("Rewrote the video");
            return lines;
        }

        /// <summary>What older backups (0.7.7) stored for a default handed to an existing file, without the file's name.</summary>
        public const string OldDefaultNote = "Default left to the subtitle file already there";

        /// <summary>The same in plain words, shown instead of <see cref="OldDefaultNote"/>.</summary>
        public const string OldDefaultNoteShown =
            "Turned off the default on the built-in subtitle, so Emby shows the subtitle file next to the video instead";

        // "turned off the default on the built-in English SDH subtitle, so Emby shows .eng.sdh.srt instead"
        private static string DefaultTurnedOff(SubtitlePlan plan, string stem)
        {
            var tracks = List(Grouped(plan.ClearDefault.Select(Label)));
            var files = plan.ClearDefault
                .Select(s => plan.LeftToExistingFiles.TryGetValue(s.Language ?? "", out var f) ? Short(f, stem) : null)
                .Where(f => f != null).Distinct().ToList();
            var subtitles = plan.ClearDefault.Count == 1 ? "subtitle" : "subtitles";
            return $"turned off the default on the built-in {tracks} {subtitles}, so Emby shows " +
                   (files.Count > 0 ? List(files) : "the subtitle file next to the video") + " instead";
        }

        /// <summary>"French", "English SDH", "English commentary", "Spanish forced".</summary>
        public static string Label(ProbeStream s)
        {
            var language = string.IsNullOrEmpty(s.Language) || s.Language == "und" ? "Unknown language" : Languages.NameOf(s.Language);
            if (s.IsCommentary) return language + " commentary";
            if (s.IsHearingImpaired) return language + " SDH";
            if (s.IsForced) return language + " forced";
            return language;
        }

        /// <summary>"Spanish", "Spanish" → "Spanish (2)", in first-seen order.</summary>
        internal static IEnumerable<string> Grouped(IEnumerable<string> labels) =>
            labels.GroupBy(l => l).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()})");

        private static string List(IEnumerable<string> items)
        {
            var all = items.ToList();
            if (all.Count <= MaxListed) return string.Join(", ", all);
            return string.Join(", ", all.Take(MaxListed)) + $" and {all.Count - MaxListed} more";
        }

        // "My Movie (2020).pgs.eng.srt" → ".pgs.eng.srt": the folder and video name are already on the row.
        private static string Short(string file, string stem)
        {
            var name = FileName(file);
            return name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) ? name.Substring(stem.Length) : name;
        }

        // The name after the last \ or /: a Windows share path stays readable on a Linux server, where \ isn't a separator.
        private static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return cut >= 0 ? path.Substring(cut + 1) : path;
        }

        private static string ModelText(OcrModels model) =>
            model == OcrModels.Both ? "both models" : model == OcrModels.Accurate ? "accurate model" : "fast model";

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
