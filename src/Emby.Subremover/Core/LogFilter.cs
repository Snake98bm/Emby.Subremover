using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>What a run-log entry is about, for the Logs tab's filter.</summary>
    public enum LogKind
    {
        /// <summary>Run start, settings, totals: shown only with "Everything".</summary>
        General,
        /// <summary>Tracks removed from or changed in a video.</summary>
        Videos,
        /// <summary>.srt files next to the videos: ads, timing, extracted files.</summary>
        SubtitleFiles,
        /// <summary>Hash-matched subtitle look-ups and downloads.</summary>
        HashDownloads,
        /// <summary>Picture subtitles turned into text.</summary>
        Ocr
    }

    /// <summary>
    /// Splits a run log into entries (a timestamped line plus the indented lines under it) and keeps the ones the Logs
    /// tab asks for. Works from the text, so it also filters logs written before it existed.
    /// </summary>
    public static class LogFilter
    {
        public const string Everything = "all";
        public const string Problems = "problems";

        private static readonly Regex EntryStart = new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} (\w+)", RegexOptions.CultureInvariant);

        private static readonly Regex VideoFile = new Regex(@"\.(mkv|mp4|m4v|avi|ts|m2ts|mov|wmv|mpg|mpeg|webm|flv)\b|\bvideos?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public sealed class Entry
        {
            public string Text { get; set; }
            public string Level { get; set; }
            public bool IsProblem => Level == "Warn" || Level == "Error" || Level == "Fatal";
            public bool IsDetail => Level == "Debug";
            public HashSet<LogKind> Kinds { get; set; }
        }

        public sealed class Result
        {
            public string Text { get; set; }
            /// <summary>How many entries each choice shows (with the details setting applied): "all", "problems", "Videos"...</summary>
            public Dictionary<string, int> Counts { get; set; }
        }

        public static List<Entry> Split(string log)
        {
            var entries = new List<Entry>();
            var current = (StringBuilder)null;
            string level = null;
            void Close()
            {
                if (current == null) return;
                var text = current.ToString();
                // The first line says what it's about; the lines under it can be anything (the settings list names them all).
                var firstLine = text.Split('\n')[0];
                entries.Add(new Entry { Text = text, Level = level, Kinds = KindsOf(firstLine) });
            }
            foreach (var line in (log ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                var start = EntryStart.Match(line);
                if (start.Success || current == null)
                {
                    Close();
                    current = new StringBuilder(line);
                    level = start.Success ? start.Groups[1].Value : null;
                }
                else
                {
                    current.Append('\n').Append(line);
                }
            }
            Close();
            // The split leaves an empty last "entry" after the final line break.
            if (entries.Count > 0 && entries[entries.Count - 1].Text.Length == 0) entries.RemoveAt(entries.Count - 1);
            return entries;
        }

        /// <summary>What an entry is about. Hash look-ups, OCR and .srt-only work are only that; a video change that also
        /// saved an .srt file counts as both.</summary>
        public static HashSet<LogKind> KindsOf(string text)
        {
            var kinds = new HashSet<LogKind>();
            if (Has(text, "hash-matched") || Has(text, "(hash ") || Has(text, "perfect match") || Has(text, "daily download limit") ||
                Has(text, "subtitle downloads allowed") || Has(text, "subtitle searches are paused"))
            {
                kinds.Add(LogKind.HashDownloads);
                return kinds;
            }
            if (text.IndexOf("OCR", StringComparison.Ordinal) >= 0)
            {
                kinds.Add(LogKind.Ocr);
                return kinds;
            }
            if (Has(text, "subtitle timing") || Has(text, "subtitle file") || Has(text, "ads in subtitle"))
            {
                kinds.Add(LogKind.SubtitleFiles);
                return kinds;
            }
            if (Has(text, ".srt")) kinds.Add(LogKind.SubtitleFiles);
            if (VideoFile.IsMatch(text)) kinds.Add(LogKind.Videos);
            if (kinds.Count == 0) kinds.Add(LogKind.General);
            return kinds;
        }

        /// <param name="show">"all", "problems", or a <see cref="LogKind"/> name.</param>
        /// <param name="hideDetails">Leave out Debug entries (every video looked at, even when nothing was done).</param>
        public static Result Apply(string log, string show, bool hideDetails)
        {
            var entries = Split(log).Where(e => !hideDetails || !e.IsDetail).ToList();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [Everything] = entries.Count,
                [Problems] = entries.Count(e => e.IsProblem)
            };
            foreach (LogKind kind in Enum.GetValues(typeof(LogKind)))
                if (kind != LogKind.General) counts[kind.ToString()] = entries.Count(e => e.Kinds.Contains(kind));

            var keep = Matcher(show);
            var shown = entries.Where(keep).Select(e => e.Text).ToList();
            var text = shown.Count > 0 ? string.Join(Environment.NewLine, shown) : "Nothing of this kind in this log.";
            return new Result { Text = text, Counts = counts };
        }

        private static Func<Entry, bool> Matcher(string show)
        {
            if (string.Equals(show, Problems, StringComparison.OrdinalIgnoreCase)) return e => e.IsProblem;
            if (!string.IsNullOrEmpty(show) && Enum.TryParse(show, true, out LogKind kind) && kind != LogKind.General)
                return e => e.Kinds.Contains(kind);
            return e => true;
        }

        private static bool Has(string text, string what) => text.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
