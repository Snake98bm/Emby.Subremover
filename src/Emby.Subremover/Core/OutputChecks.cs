using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// ffmpeg exits with 0 even when its input ends early ("File ended prematurely"), e.g. after a network drop,
    /// so every output is checked before anything is removed or replaced.
    /// </summary>
    public static class OutputChecks
    {
        private static readonly string[] TruncationMessages =
        {
            "ended prematurely", "i/o error", "error while reading", "error during demuxing", "truncat", "connection reset"
        };

        private static readonly Regex SrtTiming = new Regex(@"(\d+):(\d\d):(\d\d)[,.](\d{3})\s*-->\s*(\d+):(\d\d):(\d\d)[,.](\d{3})", RegexOptions.Compiled);
        private static readonly Regex AssDialogue = new Regex(@"^Dialogue:\s*\d+,[^,]*,(\d+):(\d\d):(\d\d)\.(\d\d)", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>Allowed shortfall in subtitle end time, for rounding and trailing empty cues.</summary>
        public const double SubtitleEndToleranceSeconds = 5;

        /// <summary>Allowed share of cues ffmpeg may legitimately drop or merge when converting.</summary>
        public const double MinimumCueShare = 0.98;

        /// <returns>The ffmpeg message that shows the input was cut short, or null.</returns>
        public static string TruncationMessage(string stderr)
        {
            if (string.IsNullOrEmpty(stderr)) return null;
            foreach (var line in stderr.Split('\n'))
            {
                foreach (var marker in TruncationMessages)
                {
                    if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return line.Trim();
                }
            }
            return null;
        }

        /// <summary>
        /// Compares an extracted .srt/.ass file with the statistics Matroska stores for the track
        /// (NUMBER_OF_FRAMES and DURATION, written by mkvmerge and ffmpeg). Tracks without those tags are only checked for content.
        /// </summary>
        /// <returns>null when the file looks complete, otherwise why not.</returns>
        /// <param name="compareCount">False for OCR output: picture tracks count "clear screen" events as frames too.</param>
        public static string CheckSubtitleFile(string path, ProbeStream track, bool isAss, bool compareCount = true)
        {
            if (!File.Exists(path)) return "file was not created";
            var text = File.ReadAllText(path);

            var cues = 0;
            var lastEnd = 0.0;
            if (isAss)
            {
                foreach (Match m in AssDialogue.Matches(text)) cues++;
                lastEnd = LastAssEnd(text);
            }
            else
            {
                foreach (Match m in SrtTiming.Matches(text))
                {
                    cues++;
                    lastEnd = Math.Max(lastEnd, Seconds(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value, 1000));
                }
            }

            if (cues == 0) return "no subtitles in the file";

            if (compareCount && int.TryParse(track.Tag("NUMBER_OF_FRAMES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expected)
                && expected > 0 && cues < expected * MinimumCueShare)
            {
                return $"only {cues} of {expected} subtitles";
            }

            var duration = ParseTagDuration(track.Tag("DURATION"));
            if (duration.HasValue && lastEnd < duration.Value - SubtitleEndToleranceSeconds)
            {
                return $"ends at {Clock(lastEnd)} but the track runs to {Clock(duration.Value)}";
            }

            return null;
        }

        // "01:40:50.753000000" (Matroska statistics tag).
        internal static double? ParseTagDuration(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var parts = value.Split(':');
            if (parts.Length != 3) return null;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) return null;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return null;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return null;
            return h * 3600 + m * 60 + s;
        }

        private static double LastAssEnd(string text)
        {
            // Dialogue: Layer,Start,End,... -> the end time is the third field.
            var last = 0.0;
            foreach (var line in text.Split('\n'))
            {
                if (!line.StartsWith("Dialogue:", StringComparison.Ordinal)) continue;
                var fields = line.Substring(9).Split(',');
                if (fields.Length < 3) continue;
                var t = fields[2].Trim().Split(':', '.');
                if (t.Length == 4) last = Math.Max(last, Seconds(t[0], t[1], t[2], t[3], 100));
            }
            return last;
        }

        private static double Seconds(string h, string m, string s, string fraction, int fractionScale) =>
            int.Parse(h, CultureInfo.InvariantCulture) * 3600 + int.Parse(m, CultureInfo.InvariantCulture) * 60
            + int.Parse(s, CultureInfo.InvariantCulture) + int.Parse(fraction, CultureInfo.InvariantCulture) / (double)fractionScale;

        private static string Clock(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
    }
}
