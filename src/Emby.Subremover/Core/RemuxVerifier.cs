using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Subremover.Core
{
    public static class RemuxVerifier
    {
        public const double DurationToleranceSeconds = 5;

        /// <returns>null when the new file looks correct, otherwise the reason it was rejected.</returns>
        public static string Verify(ProbeResult before, ProbeResult after, int removedSubtitles, long beforeBytes, long afterBytes)
        {
            if (after == null || after.streams.Count == 0) return "new file has no streams";

            if (after.Count("video") != before.Count("video"))
                return $"video track count changed ({before.Count("video")} -> {after.Count("video")})";

            if (after.Count("audio") != before.Count("audio"))
                return $"audio track count changed ({before.Count("audio")} -> {after.Count("audio")})";

            var expectedSubs = before.Count("subtitle") - removedSubtitles;
            if (after.Count("subtitle") != expectedSubs)
                return $"expected {expectedSubs} subtitle tracks but found {after.Count("subtitle")}";

            var d1 = before.DurationSeconds;
            var d2 = after.DurationSeconds;
            if (d1.HasValue && d2.HasValue)
            {
                // A stream copy keeps the length; a few seconds covers rounding. A percentage would let a
                // 2-hour movie lose over a minute at the end without being noticed.
                if (Math.Abs(d1.Value - d2.Value) > DurationToleranceSeconds)
                    return $"duration changed ({d1:F1}s -> {d2:F1}s)";
            }

            if (afterBytes < beforeBytes / 2)
                return $"new file is less than half the original size ({beforeBytes} -> {afterBytes} bytes)";

            return null;
        }

        /// <returns>A problem unless every stream in <paramref name="languages"/> (by output index) has that language.</returns>
        public static string CheckLanguages(ProbeResult after, IDictionary<int, string> languages)
        {
            foreach (var expected in languages)
            {
                var stream = after.streams.FirstOrDefault(s => s.index == expected.Key);
                if (stream == null || !string.Equals(stream.Language, expected.Value, StringComparison.OrdinalIgnoreCase))
                    return $"expected stream #{expected.Key} to have language {expected.Value} but found {stream?.Language ?? "no stream"}";
            }
            return null;
        }

        /// <returns>A problem if any of the streams (by output index) is still flagged default.</returns>
        public static string CheckNotDefault(ProbeResult after, IEnumerable<int> outputIndexes)
        {
            var still = outputIndexes.Where(i => after.streams.Any(s => s.index == i && s.Disposition("default"))).ToList();
            return still.Count == 0 ? null : $"expected #{string.Join(", #", still)} to lose the default flag";
        }

        /// <returns>A problem unless the subtitle at <paramref name="outputIndex"/> is the only one flagged default.</returns>
        public static string CheckDefault(ProbeResult after, int outputIndex)
        {
            var flagged = after.streams.Where(s => s.IsSubtitle && s.Disposition("default")).Select(s => s.index).ToList();
            return flagged.Count == 1 && flagged[0] == outputIndex
                ? null
                : $"expected only subtitle #{outputIndex} to be default but found {(flagged.Count == 0 ? "none" : "#" + string.Join(", #", flagged))}";
        }
    }
}
