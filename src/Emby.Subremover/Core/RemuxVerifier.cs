using System;

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
    }
}
