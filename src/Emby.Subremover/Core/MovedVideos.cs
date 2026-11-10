using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Videos moved on the share since an earlier run, or whose folder was: their "already done" record, backup,
    /// "restored" mark and the subtitle files SubRemover made for them follow them to the new path. Found by file name among
    /// the videos Emby lists now - no folder is scanned - and, for "already done", by the same size and date, which a move keeps.
    /// </summary>
    public static class MovedVideos
    {
        public sealed class Move
        {
            public string From { get; set; }
            public string To { get; set; }
        }

        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

        /// <param name="current">The videos this run checks, at their paths now.</param>
        /// <param name="exists">Whether a file is there; only asked about a recorded path once a video of the same name turns up.</param>
        /// <param name="stampOf">A file's size and date; null when it can't be read.</param>
        /// <returns>Each video that was found at a new path, once.</returns>
        public static List<Move> Reconnect(SubremoverState state, IReadOnlyCollection<string> current, Func<string, bool> exists, Func<string, FileStamp> stampOf)
        {
            var now = new HashSet<string>(current, Paths);
            var byName = current.GroupBy(Path.GetFileName, Paths).ToDictionary(g => g.Key, g => g.ToList(), Paths);
            List<string> SameName(string old) => byName.TryGetValue(Path.GetFileName(old), out var found) ? found : new List<string>();
            var moved = new Dictionary<string, string>(Paths);

            // Already done: the same file, gone from where it was.
            foreach (var old in state.Processed.Keys.Where(k => !now.Contains(k)).ToList())
            {
                var stamp = state.Processed[old];
                var to = SameName(old).FirstOrDefault(c => !state.Processed.ContainsKey(c) && stamp.Matches(stampOf(c)));
                if (to == null || exists(old)) continue;
                state.Processed.Remove(old);
                state.Processed[to] = stamp;
                moved[old] = to;
            }

            // A backup has no size or date of its own video to go by: the video moved with it, or the only one of that name.
            string NewPath(string old)
            {
                if (moved.TryGetValue(old, out var to)) return to;
                var same = SameName(old);
                return same.Count == 1 && !exists(old) ? same[0] : null;
            }

            foreach (var backup in state.Backups.Where(b => !now.Contains(b.OriginalPath)))
            {
                var to = NewPath(backup.OriginalPath);
                if (to == null) continue;
                // Next to the video it moved along; in the backup folder it stayed, and only where it goes back changes.
                if (Paths.Equals(backup.BackupPath, backup.OriginalPath + BackupPaths.SideBySideSuffix))
                {
                    if (!exists(to + BackupPaths.SideBySideSuffix)) continue;
                    backup.BackupPath = to + BackupPaths.SideBySideSuffix;
                }
                moved[backup.OriginalPath] = to;
                backup.OriginalPath = to;
            }

            for (var i = 0; i < state.Restored.Count; i++)
            {
                var old = state.Restored[i];
                if (now.Contains(old)) continue;
                var to = NewPath(old);
                if (to == null || state.IsRestored(to)) continue;
                state.Restored[i] = to;
                moved[old] = to;
            }

            // The subtitle files made for a video sit next to it, named after it ("Movie.eng.srt"), and moved with it.
            foreach (var m in moved.Where(m => !Paths.Equals(Path.GetDirectoryName(m.Key), Path.GetDirectoryName(m.Value))))
            {
                var oldFolder = Path.GetDirectoryName(m.Key);
                var stem = Path.GetFileNameWithoutExtension(m.Key) + ".";
                foreach (var made in state.MadeFiles.Where(f => Paths.Equals(Path.GetDirectoryName(f), oldFolder)
                                                                && Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var newPath = Path.Combine(Path.GetDirectoryName(m.Value), Path.GetFileName(made));
                    state.MadeFiles.Remove(made);
                    state.MadeFiles.Add(newPath);
                    if (state.OcrFiles.TryGetValue(made, out var record))
                    {
                        state.OcrFiles.Remove(made);
                        state.OcrFiles[newPath] = record;
                    }
                }
            }

            return moved.Select(m => new Move { From = m.Key, To = m.Value }).ToList();
        }

        /// <summary>A file's size and date, or null when it can't be read: it then just isn't matched.</summary>
        public static FileStamp StampOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? FileStamp.Of(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
