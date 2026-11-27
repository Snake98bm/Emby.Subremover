using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Emby.Subremover.Core
{
    /// <summary>One run in which a video failed, and why.</summary>
    public class FailedAttempt
    {
        public DateTime Utc { get; set; }
        public string Reason { get; set; }

        /// <summary>The run log with the details.</summary>
        public string LogFile { get; set; }
    }

    /// <summary>A video that failed in one or more runs in a row, for the Errors tab.</summary>
    public class FailedVideo
    {
        /// <summary>Runs in a row it failed in, since it last worked, was retried or its file changed.</summary>
        public int Count { get; set; }

        /// <summary>The file as it was when it last failed: a changed file (replaced, re-ripped) gets tried again.</summary>
        public FileStamp Stamp { get; set; }

        public DateTime FirstUtc { get; set; }
        public DateTime LastUtc { get; set; }

        /// <summary>The newest attempts, oldest first (at most <see cref="FailedVideos.KeepAttempts"/>).</summary>
        public List<FailedAttempt> Attempts { get; set; } = new List<FailedAttempt>();

        /// <summary>A method, so no JSON reader or writer ever sees it.</summary>
        public string LastReason() => Attempts?.LastOrDefault()?.Reason;
    }

    /// <summary>
    /// "Stop trying a video after this many failed runs": the bookkeeping behind it. Pure, so it can be tested without Emby;
    /// the caller holds the state's lock and saves it.
    /// </summary>
    public static class FailedVideos
    {
        public const int KeepAttempts = 10;

        /// <summary>
        /// A run where this many videos failed, and at least half of the ones it looked at, points at the share, the
        /// disk or ffmpeg rather than at the videos: its failures don't count towards stopping (one run in Nov 2026 had 7,993).
        /// </summary>
        public const int WholeRunFailureMinimum = 25;

        public static bool LooksLikeWholeRunProblem(int failed, int looked) =>
            failed >= WholeRunFailureMinimum && failed * 2 >= looked;

        /// <summary>Whether a run should leave this video alone: it failed <paramref name="maxAttempts"/> runs in a row and the file hasn't changed since.</summary>
        public static bool ShouldSkip(SubremoverState state, string path, FileStamp now, int maxAttempts)
        {
            if (maxAttempts <= 0 || state.Failures == null) return false;
            return state.Failures.TryGetValue(path, out var record)
                   && record.Count >= maxAttempts
                   && (record.Stamp == null || now == null || record.Stamp.Matches(now));
        }

        /// <returns>The record after this failure.</returns>
        public static FailedVideo Record(SubremoverState state, string path, FileStamp stamp, string reason, string logFile, DateTime nowUtc)
        {
            if (state.Failures == null) state.Failures = new Dictionary<string, FailedVideo>(StringComparer.OrdinalIgnoreCase);
            if (!state.Failures.TryGetValue(path, out var record)
                || (record.Stamp != null && stamp != null && !record.Stamp.Matches(stamp)))
            {
                record = new FailedVideo { FirstUtc = nowUtc };
                state.Failures[path] = record;
            }
            record.Count++;
            record.Stamp = stamp;
            record.LastUtc = nowUtc;
            record.Attempts.Add(new FailedAttempt { Utc = nowUtc, Reason = string.IsNullOrWhiteSpace(reason) ? "failed (no reason was given; see the run log)" : reason.Trim(), LogFile = logFile });
            if (record.Attempts.Count > KeepAttempts) record.Attempts.RemoveRange(0, record.Attempts.Count - KeepAttempts);
            return record;
        }

        /// <returns>true when the video had a record.</returns>
        public static bool Forget(SubremoverState state, string path) => state.Failures != null && state.Failures.Remove(path);

        /// <summary>Short enough for one table cell: ffmpeg's error output can run to pages.</summary>
        public static string Shorten(string reason, int max = 600)
        {
            if (string.IsNullOrEmpty(reason)) return reason;
            var oneLine = string.Join(" ", reason.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
        }
    }

    /// <summary>
    /// "subremover-failures.log" in Emby's log folder: every failure, skip and retry, one line each, so the Errors tab can
    /// show what's been going on across runs. A .log, so it isn't taken for a run log; kept to about 1 MB.
    /// </summary>
    public sealed class FailureLog
    {
        public const string FileName = "subremover-failures.log";
        private const long MaxBytes = 1_000_000;
        private static readonly object Lock = new object();

        private readonly string _path;

        public FailureLog(string logDirectory)
        {
            _path = Path.Combine(logDirectory, FileName);
        }

        public string FilePath => _path;

        public void Write(string line, DateTime? nowLocal = null)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    var stamp = (nowLocal ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    File.AppendAllText(_path, stamp + "  " + line.Replace("\r", " ").Replace("\n", " ") + Environment.NewLine, new UTF8Encoding(false));
                    TrimIfLong();
                }
            }
            catch (Exception)
            {
                // A log that can't be written must never stop a run.
            }
        }

        private void TrimIfLong()
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= MaxBytes) return;
            var lines = File.ReadAllLines(_path);
            var keep = new List<string>();
            long size = 0;
            for (var i = lines.Length - 1; i >= 0 && size < MaxBytes / 2; i--)
            {
                keep.Insert(0, lines[i]);
                size += lines[i].Length + 2;
            }
            File.WriteAllLines(_path, new[] { "… (older lines removed)" }.Concat(keep), new UTF8Encoding(false));
        }

        /// <summary>The newest lines first, for the Errors tab.</summary>
        public string ReadNewestFirst(int maxLines = 500)
        {
            try
            {
                lock (Lock)
                {
                    if (!File.Exists(_path)) return "";
                    using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var lines = reader.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        return string.Join(Environment.NewLine, lines.Reverse().Take(maxLines));
                    }
                }
            }
            catch (Exception ex)
            {
                return "Couldn't read " + FileName + ": " + ex.Message;
            }
        }

        public string ReadAll()
        {
            lock (Lock)
            {
                if (!File.Exists(_path)) return "";
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }
    }
}
