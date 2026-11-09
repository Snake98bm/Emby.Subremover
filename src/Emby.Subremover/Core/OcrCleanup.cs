using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Tidies the .srt that ffmpeg's OCR writes.
    /// Timing (every language): Blu-ray (PGS) subtitles store no duration, only a later "clear the screen" event, so
    /// ffmpeg writes each cue as lasting 1193 hours and adds an empty cue where the screen clears. Each cue is ended
    /// where the next one starts, and the empty cues are dropped.
    /// Text (English only): fixes the usual misreads, e.g. "1 don't", "l think", "/'m", "[t's" for I, a lost "[" or "]".
    /// </summary>
    public static class OcrCleanup
    {
        /// <summary>A cue longer than this has no real end time.</summary>
        private const long UnknownDurationMs = 24L * 60 * 60 * 1000;

        /// <summary>How long the very last cue stays when nothing says when it ends.</summary>
        private const long LastCueMs = 4000;

        private static readonly Regex TimingLine = new Regex(@"^\s*(\d+):(\d\d):(\d\d)[,.](\d{3})\s*-->\s*(\d+):(\d\d):(\d\d)[,.](\d{3})", RegexOptions.Compiled);
        private static readonly Regex IndexLine = new Regex(@"^\s*\d+\s*$", RegexOptions.Compiled);

        public class Cue
        {
            public long StartMs { get; set; }
            public long EndMs { get; set; }
            public List<string> Lines { get; } = new List<string>();
            public bool IsEmpty => Lines.All(l => string.IsNullOrWhiteSpace(StripTags(l)));
        }

        public static string Clean(string srt, string tesseractLanguage) => Write(CleanCues(srt, tesseractLanguage));

        /// <summary>Two readings of the same track (fast and accurate model): each subtitle takes the better one.</summary>
        public static string CleanBoth(string first, string second, string tesseractLanguage) =>
            Write(OcrChooser.Pick(CleanCues(first, tesseractLanguage), CleanCues(second, tesseractLanguage), IsEnglish(tesseractLanguage)));

        private static List<Cue> CleanCues(string srt, string tesseractLanguage)
        {
            var cues = Parse(srt);
            FixTimings(cues);
            cues = MergeRepeats(cues.Where(c => !c.IsEmpty).ToList());
            if (IsEnglish(tesseractLanguage)) EnglishText.Fix(cues);
            return cues;
        }

        private static bool IsEnglish(string tesseractLanguage) => string.Equals(tesseractLanguage, "eng", StringComparison.OrdinalIgnoreCase);

        public static List<Cue> Parse(string srt)
        {
            var cues = new List<Cue>();
            var lines = (srt ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Cue current = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var m = TimingLine.Match(lines[i]);
                if (m.Success)
                {
                    // The cue number on the line before belongs to this cue, not to the text of the one before.
                    if (current != null && current.Lines.Count > 0 && IndexLine.IsMatch(current.Lines[current.Lines.Count - 1]))
                        current.Lines.RemoveAt(current.Lines.Count - 1);
                    current = new Cue { StartMs = Ms(m, 1), EndMs = Ms(m, 5) };
                    cues.Add(current);
                }
                else if (current != null)
                {
                    current.Lines.Add(lines[i].TrimEnd());
                }
            }

            foreach (var cue in cues)
            {
                while (cue.Lines.Count > 0 && cue.Lines[cue.Lines.Count - 1].Trim().Length == 0) cue.Lines.RemoveAt(cue.Lines.Count - 1);
                while (cue.Lines.Count > 0 && cue.Lines[0].Trim().Length == 0) cue.Lines.RemoveAt(0);
            }
            return cues;
        }

        /// <summary>Ends each cue without a real end time where the next one (often an empty "clear") starts.</summary>
        public static void FixTimings(List<Cue> cues)
        {
            for (var i = 0; i < cues.Count; i++)
            {
                var cue = cues[i];
                if (cue.EndMs > cue.StartMs && cue.EndMs - cue.StartMs < UnknownDurationMs) continue;

                var next = cues.Skip(i + 1).FirstOrDefault(c => c.StartMs > cue.StartMs);
                cue.EndMs = next != null ? next.StartMs : cue.StartMs + LastCueMs;
            }
        }

        /// <summary>
        /// A disc sometimes sends the same picture again a frame later; that would show as a one-frame cue followed
        /// by an identical one, so they become one cue.
        /// </summary>
        public static List<Cue> MergeRepeats(List<Cue> cues)
        {
            var merged = new List<Cue>();
            foreach (var cue in cues)
            {
                var last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                // Only when nothing cleared the screen in between: the same line shown again after a clear stays two.
                if (last != null && cue.StartMs <= last.EndMs && cue.Lines.SequenceEqual(last.Lines))
                {
                    last.EndMs = Math.Max(last.EndMs, cue.EndMs);
                    continue;
                }
                merged.Add(cue);
            }
            return merged;
        }

        public static string Write(IList<Cue> cues)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < cues.Count; i++)
            {
                sb.Append(i + 1).Append('\n');
                sb.Append(Clock(cues[i].StartMs)).Append(" --> ").Append(Clock(cues[i].EndMs)).Append('\n');
                foreach (var line in cues[i].Lines) sb.Append(line).Append('\n');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static long Ms(Match m, int group) =>
            long.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) * 3600000
            + long.Parse(m.Groups[group + 1].Value, CultureInfo.InvariantCulture) * 60000
            + long.Parse(m.Groups[group + 2].Value, CultureInfo.InvariantCulture) * 1000
            + long.Parse(m.Groups[group + 3].Value, CultureInfo.InvariantCulture);

        private static string Clock(long ms) =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, ms % 1000);

        internal static string StripTags(string line) => Regex.Replace(line ?? string.Empty, @"<[^>]*>|\{[^}]*\}", "");
    }
}
