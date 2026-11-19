using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>One cue's time on screen, in milliseconds.</summary>
    public struct CueTime
    {
        public long StartMs;
        public long EndMs;

        public CueTime(long startMs, long endMs)
        {
            StartMs = startMs;
            EndMs = endMs;
        }
    }

    /// <summary>Reads the times of an .srt file's cues, and moves them all by the same amount (never stretches them).</summary>
    public static class SrtTimes
    {
        // "00:01:02,345 --> 00:01:04,000", also with '.' and one- or two-digit hours, and anything after (position tags).
        private static readonly Regex TimingLine = new Regex(
            @"^(?<lead>\s*)(?<a>\d{1,3}:\d{1,2}:\d{1,2}[,.]\d{1,3})(?<mid>\s*-->\s*)(?<b>\d{1,3}:\d{1,2}:\d{1,2}[,.]\d{1,3})(?<rest>.*)$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static List<CueTime> Read(string srt)
        {
            var cues = new List<CueTime>();
            foreach (Match m in TimingLine.Matches(srt ?? string.Empty))
            {
                var a = ParseTime(m.Groups["a"].Value);
                var b = ParseTime(m.Groups["b"].Value);
                if (a == null || b == null || b.Value <= a.Value) continue;
                cues.Add(new CueTime(a.Value, b.Value));
            }
            return cues;
        }

        /// <summary>
        /// The same text with every timing moved by <paramref name="offsetMs"/>; nothing else changes. A time that would be
        /// before the start of the video becomes 0.
        /// </summary>
        public static string Shift(string srt, long offsetMs)
        {
            return TimingLine.Replace(srt ?? string.Empty, m =>
            {
                var a = ParseTime(m.Groups["a"].Value);
                var b = ParseTime(m.Groups["b"].Value);
                if (a == null || b == null) return m.Value;
                var comma = m.Groups["a"].Value.Contains(".") ? '.' : ',';
                return m.Groups["lead"].Value + Format(Math.Max(0, a.Value + offsetMs), comma) + m.Groups["mid"].Value +
                       Format(Math.Max(0, b.Value + offsetMs), comma) + m.Groups["rest"].Value;
            });
        }

        internal static long? ParseTime(string text)
        {
            var parts = text.Split(':', ',', '.');
            if (parts.Length != 4) return null;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ||
                !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var f)) return null;
            // ",5" is half a second, ",05" 50 ms.
            var ms = parts[3].Length == 1 ? f * 100 : parts[3].Length == 2 ? f * 10 : f;
            return ((h * 60L + m) * 60 + s) * 1000 + ms;
        }

        internal static string Format(long ms, char comma = ',') =>
            string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}{3}{4:000}", ms / 3600000, ms / 60000 % 60, ms / 1000 % 60, comma, ms % 1000);

        /// <summary>"+2.35 s", "-0.80 s".</summary>
        public static string Describe(long offsetMs) =>
            (offsetMs >= 0 ? "+" : "-") + (Math.Abs(offsetMs) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// How loud speech is in each 20 ms of a video's dialogue audio: the log of the energy between 200 Hz and 3.5 kHz,
    /// scaled so a typical moment is 0. Made from 8 kHz mono 16-bit samples (ffmpeg's s16le).
    /// </summary>
    public sealed class SpeechTrack
    {
        public const int FrameMs = 20;
        public const int SampleRate = 8000;
        private const int SamplesPerFrame = SampleRate * FrameMs / 1000;

        public float[] Level { get; }

        private SpeechTrack(float[] level)
        {
            Level = level;
        }

        public double Seconds => Level.Length * FrameMs / 1000.0;

        /// <summary>Reads raw 8 kHz mono s16le samples to the end of the stream.</summary>
        public static SpeechTrack FromPcm(Stream pcm)
        {
            var energies = new List<float>(1 << 16);
            var buffer = new byte[SamplesPerFrame * 2 * 64];
            double sum = 0;
            var n = 0;
            int odd = -1; // a byte left over when a read ends half-way through a sample
            int read;
            while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0)
            {
                var i = 0;
                if (odd >= 0)
                {
                    Add((short)(odd | (buffer[0] << 8)));
                    odd = -1;
                    i = 1;
                }
                for (; i + 1 < read; i += 2) Add((short)(buffer[i] | (buffer[i + 1] << 8)));
                if (i < read) odd = buffer[i];
            }
            return FromEnergies(energies);

            void Add(short sample)
            {
                sum += (double)sample * sample;
                if (++n < SamplesPerFrame) return;
                energies.Add((float)Math.Log10(1 + sum / SamplesPerFrame));
                sum = 0;
                n = 0;
            }
        }

        /// <summary>From the log energy of each 20 ms frame (for tests).</summary>
        public static SpeechTrack FromEnergies(IList<float> logEnergy)
        {
            var level = logEnergy.ToArray();
            if (level.Length == 0) return new SpeechTrack(level);

            // A local scale, so a loud action scene doesn't count as an hour of talking and quiet dialogue still counts:
            // each frame against the 30 seconds around it.
            const int window = 30000 / FrameMs;
            var prefix = new double[level.Length + 1];
            var prefixSq = new double[level.Length + 1];
            for (var i = 0; i < level.Length; i++)
            {
                prefix[i + 1] = prefix[i] + level[i];
                prefixSq[i + 1] = prefixSq[i] + (double)level[i] * level[i];
            }
            var scaled = new float[level.Length];
            for (var i = 0; i < level.Length; i++)
            {
                var a = Math.Max(0, i - window / 2);
                var b = Math.Min(level.Length, i + window / 2);
                var count = b - a;
                var mean = (prefix[b] - prefix[a]) / count;
                var variance = Math.Max(0, (prefixSq[b] - prefixSq[a]) / count - mean * mean);
                var sd = Math.Sqrt(variance);
                var z = sd < 0.05 ? 0 : (level[i] - mean) / sd;
                scaled[i] = (float)Math.Max(-3, Math.Min(3, z));
            }
            return new SpeechTrack(scaled);
        }
    }

    public enum TimingVerdict
    {
        /// <summary>Already lines up with the speech.</summary>
        InSync,
        /// <summary>Off by the same amount all the way through: moving it fixes it.</summary>
        Shift,
        /// <summary>Different parts are off by different amounts (another frame rate or cut): moving can't fix it.</summary>
        Drifts,
        /// <summary>No clear match with the speech (too few cues, music, another language's dub...).</summary>
        Unsure
    }

    public sealed class TimingResult
    {
        public TimingVerdict Verdict { get; set; }

        /// <summary>What to add to every time so the file lines up (0 unless <see cref="TimingVerdict.Shift"/>).</summary>
        public long OffsetMs { get; set; }

        /// <summary>The best match found, whatever the verdict.</summary>
        public long BestOffsetMs { get; set; }

        /// <summary>How far the best match stands out from all other offsets (standard deviations).</summary>
        public double Confidence { get; set; }

        /// <summary>The best offset of the start, middle and end of the file.</summary>
        public long[] PartOffsetsMs { get; set; } = new long[0];

        public string Describe()
        {
            var parts = PartOffsetsMs.Length > 1 ? "; start/middle/end " + string.Join(" / ", PartOffsetsMs.Select(SrtTimes.Describe)) : "";
            var match = $"match {Confidence.ToString("0.0", CultureInfo.InvariantCulture)}";
            switch (Verdict)
            {
                case TimingVerdict.InSync: return $"in sync (best {SrtTimes.Describe(BestOffsetMs)}, {match})";
                case TimingVerdict.Shift: return $"off by {SrtTimes.Describe(-OffsetMs)}, moving every cue {SrtTimes.Describe(OffsetMs)} ({match}{parts})";
                case TimingVerdict.Drifts: return $"timing drifts or jumps ({Needs()}), needs more than a shift, left alone ({match}{parts})";
                default: return $"no clear match with the speech, left alone (best {SrtTimes.Describe(BestOffsetMs)}, {match})";
            }
        }

        /// <summary>
        /// What the file needs, in a few words: "in sync", "off by +2.30 s", "drifts steadily from -0.10 s to +3.70 s
        /// (another frame rate or speed)", "jumps -6.70 s near the end (another cut)", "no clear match".
        /// </summary>
        public string Needs()
        {
            switch (Verdict)
            {
                case TimingVerdict.InSync: return "in sync";
                case TimingVerdict.Shift: return "off by " + SrtTimes.Describe(-OffsetMs);
                case TimingVerdict.Unsure: return "no clear match";
            }
            var p = PartOffsetsMs ?? new long[0];
            if (p.Length != 3) return "drifts";
            long a = -p[0], b = -p[1], c = -p[2];   // how far the file is off, like "off by"
            const long same = SubtitleSync.MaxPartSpreadMs;
            if (Math.Abs(a - b) <= same && Math.Abs(c - b) > same) return $"off by {SrtTimes.Describe(a)}, then jumps to {SrtTimes.Describe(c)} near the end (another cut)";
            if (Math.Abs(b - c) <= same && Math.Abs(a - b) > same) return $"off by {SrtTimes.Describe(a)} at the start, then {SrtTimes.Describe(c)} (another cut)";
            if (Math.Abs(a - c) <= same && Math.Abs(b - a) > same) return $"off by {SrtTimes.Describe(a)}, {SrtTimes.Describe(b)} in the middle only (another cut)";
            if ((a < b && b < c) || (a > b && b > c))
                return $"drifts steadily from {SrtTimes.Describe(a)} to {SrtTimes.Describe(c)} (another frame rate or speed)";
            return $"off by {SrtTimes.Describe(a)} / {SrtTimes.Describe(b)} / {SrtTimes.Describe(c)} at start / middle / end";
        }
    }

    /// <summary>
    /// Finds the one offset that best lines a subtitle file's cues up with the speech in the video: the offset where the
    /// cues cover the most speech. Then checks the start, middle and end of the file agree on it; if they don't, the file
    /// needs stretching or cutting, which is left alone.
    /// </summary>
    public static class SubtitleSync
    {
        /// <summary>Smaller offsets are left as they are: nobody sees them, and the measurement isn't that exact.</summary>
        public const long DefaultMinShiftMs = 300;

        /// <summary>How far the start, middle and end may disagree and it still counts as one offset.</summary>
        public const long MaxPartSpreadMs = 400;

        /// <summary>A file whose start, middle and end are each off by less than this counts as in sync, even when they disagree.</summary>
        public const long NearSyncMs = 500;

        /// <summary>How clearly the best offset has to stand out.</summary>
        public const double MinConfidence = 6.0;

        public const int MinCues = 20;

        public static TimingResult Measure(SpeechTrack speech, IList<CueTime> cues, long maxOffsetMs = 120000, long minShiftMs = DefaultMinShiftMs)
        {
            const int frame = SpeechTrack.FrameMs;
            var result = new TimingResult { Verdict = TimingVerdict.Unsure };
            if (cues.Count < MinCues || speech.Level.Length == 0) return result;

            var prefix = new double[speech.Level.Length + 1];
            for (var i = 0; i < speech.Level.Length; i++) prefix[i + 1] = prefix[i] + speech.Level[i];

            var maxSteps = (int)(maxOffsetMs / frame);
            var all = Scores(prefix, cues, 0, cues.Count, -maxSteps, maxSteps);
            var best = ArgMax(all);
            var bestStep = best - maxSteps;
            result.BestOffsetMs = bestStep * (long)frame;

            // How the best stands out from the offsets more than 2 seconds away from it.
            var others = all.Where((s, i) => Math.Abs(i - best) * frame > 2000).ToList();
            if (others.Count < 10) return result;
            var mean = others.Average();
            var sd = Math.Sqrt(others.Sum(s => (s - mean) * (s - mean)) / others.Count);
            result.Confidence = sd <= 1e-9 ? 0 : (all[best] - mean) / sd;
            if (result.Confidence < MinConfidence) return result;

            // Start, middle and end on their own, each searched widely around the overall best.
            const int parts = 3;
            var partSteps = (int)(20000 / frame);
            var offsets = new List<long>();
            for (var p = 0; p < parts; p++)
            {
                var from = cues.Count * p / parts;
                var to = cues.Count * (p + 1) / parts;
                var scores = Scores(prefix, cues, from, to, bestStep - partSteps, bestStep + partSteps);
                offsets.Add((ArgMax(scores) + bestStep - partSteps) * (long)frame);
            }
            result.PartOffsetsMs = offsets.ToArray();
            if (offsets.Max() - offsets.Min() > MaxPartSpreadMs)
            {
                // Start, middle and end all within half a second: nobody can see it, and it's mostly the measuring's own
                // wobble. Counted as in sync, so such a file is never deleted as an out-of-sync or worse copy.
                if (offsets.All(o => Math.Abs(o) < NearSyncMs))
                {
                    result.Verdict = TimingVerdict.InSync;
                    return result;
                }
                result.Verdict = TimingVerdict.Drifts;
                return result;
            }

            if (Math.Abs(result.BestOffsetMs) < minShiftMs)
            {
                result.Verdict = TimingVerdict.InSync;
                return result;
            }
            result.Verdict = TimingVerdict.Shift;
            // Rounded to 10 ms: nobody can tell, and the file reads better.
            result.OffsetMs = (long)Math.Round(result.BestOffsetMs / 10.0) * 10;
            return result;
        }

        // How well cues [from, to) start where speech starts, with the cues moved by each step from minStep to maxStep: for
        // each cue, the speech level in its first second minus the level in the 300 ms before it. Only the start counts:
        // subtitles appear as the line is spoken but stay up a while after it, by however long the subtitler liked, so
        // the end of a cue says little and the whole cue gives a flat-topped peak rather than a sharp one.
        private static double[] Scores(double[] prefix, IList<CueTime> cues, int from, int to, int minStep, int maxStep)
        {
            const int frame = SpeechTrack.FrameMs;
            const int before = 300 / frame;
            var frames = prefix.Length - 1;
            var scores = new double[maxStep - minStep + 1];
            var counted = 0;
            for (var c = from; c < to; c++)
            {
                var a = (int)(cues[c].StartMs / frame);
                var inside = (int)(Math.Min(cues[c].EndMs - cues[c].StartMs, 1000) / frame);
                if (inside <= 0) continue;
                counted++;
                for (var k = 0; k < scores.Length; k++)
                {
                    var s = a + minStep + k;
                    // Outside the video counts as an average moment (0).
                    var speech = prefix[Clamp(s + inside, frames)] - prefix[Clamp(s, frames)];
                    var quiet = prefix[Clamp(s, frames)] - prefix[Clamp(s - before, frames)];
                    scores[k] += speech / inside - quiet / before;
                }
            }
            if (counted > 0) for (var k = 0; k < scores.Length; k++) scores[k] /= counted;
            return scores;
        }

        private static int Clamp(int i, int frames) => i < 0 ? 0 : i > frames ? frames : i;

        private static int ArgMax(double[] values)
        {
            var best = 0;
            for (var i = 1; i < values.Length; i++)
                if (values[i] > values[best]) best = i;
            return best;
        }
    }
}
