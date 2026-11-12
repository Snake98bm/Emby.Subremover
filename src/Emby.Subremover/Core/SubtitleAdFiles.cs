using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Logging;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Goes through the .srt files next to videos and takes the ads out (or, counting only, logs what it would take out).
    /// Each changed file is backed up first and shows on the restore list. Used by the "Remove ads from subtitle files" task
    /// and at the end of each run for the videos it checked.
    /// </summary>
    public sealed class SubtitleAdFiles
    {
        public const string BackupNote = "Subtitle file: removed {0} ad line{1}";

        private static readonly HashSet<string> OcrMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pgs", "dvd", "dvb", "xsub" };

        private readonly SubtitleAdRules _rules;
        private readonly bool _countOnly;
        private readonly BackupMode _backupMode;
        private readonly string _backupFolder;
        private readonly ILogger _log;
        private readonly object _lock = new object();

        public SubtitleAdFiles(SubtitleAdRules rules, bool countOnly, BackupMode backupMode, string backupFolder, ILogger log)
        {
            _rules = rules;
            _countOnly = countOnly;
            _backupMode = backupMode;
            _backupFolder = backupFolder;
            _log = log;
        }

        public SubtitleAdSuggestions Suggestions { get; } = new SubtitleAdSuggestions();

        public int FilesChecked;
        public int FilesWithAds;
        public int CuesRemoved;
        public int Failed;
        public int Skipped;

        /// <summary>The .srt files that belong to a video: "Movie.srt", "Movie.en.srt", "Movie.eng.sdh.default.srt".</summary>
        public static List<string> FilesFor(string videoPath)
        {
            var dir = Path.GetDirectoryName(videoPath);
            var name = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateFiles(dir, name + "*.srt")
                .Where(f =>
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    return string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)
                           || stem.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        /// <summary>OCR files are left alone: SubRemover made them, and redoing an OCR file goes by its date and size.</summary>
        public static bool IsOcrFile(string path, string videoPath, SubremoverState state)
        {
            if (state.OcrFiles.ContainsKey(path)) return true;
            var stem = Path.GetFileNameWithoutExtension(path);
            var name = Path.GetFileNameWithoutExtension(videoPath);
            if (stem.Length <= name.Length) return false;
            return stem.Substring(name.Length).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).Any(OcrMarkers.Contains);
        }

        /// <summary>Call first: when the list or switches changed, every file is checked again.</summary>
        public void Begin(SubremoverState state)
        {
            if (_countOnly) return;
            lock (state)
            {
                if (state.AdRulesSignature == _rules.Signature) return;
                state.AdChecked.Clear();
                state.AdRulesSignature = _rules.Signature;
            }
        }

        /// <summary>Whether the file has to be read: new, or changed since it was last checked with these rules.</summary>
        public bool NeedsCheck(string path, SubremoverState state)
        {
            if (_countOnly) return true;
            FileStamp stamp;
            lock (state)
            {
                if (state.AdRulesSignature != _rules.Signature || !state.AdChecked.TryGetValue(path, out stamp)) return true;
            }
            return !stamp.Matches(MovedVideos.StampOrNull(path));
        }

        /// <summary>Checks one file, takes the ads out (unless counting only) and notes it as checked.</summary>
        public void Clean(string path, string videoPath, SubremoverState state)
        {
            if (state.IsRestored(path) || IsOcrFile(path, videoPath, state))
            {
                System.Threading.Interlocked.Increment(ref Skipped);
                return;
            }
            System.Threading.Interlocked.Increment(ref FilesChecked);
            try
            {
                var file = SrtAdCleaner.Read(path);
                var result = SrtAdCleaner.Clean(file.Text, _rules);
                Suggestions.Add(SubtitleAdSuggestions.TitleOf(path), result);
                if (result.Removed.Count == 0)
                {
                    Note(path, state);
                    return;
                }

                System.Threading.Interlocked.Increment(ref FilesWithAds);
                System.Threading.Interlocked.Add(ref CuesRemoved, result.Removed.Count);
                // One pre-built string: the subtitle text may contain braces.
                var lines = string.Concat(result.Removed.Select(r => $"\n    cue {r.Number} of {result.Cues} at {r.Time} [{r.Rule}]: {SrtAdCleaner.ForLog(r.Text)}"));
                if (_countOnly)
                {
                    _log.Info($"SubRemover: {path} has {result.Removed.Count} ad line{(result.Removed.Count == 1 ? "" : "s")} (not removed: count only):{lines}");
                    return;
                }

                var temp = path + Leftovers.TempSuffix;
                File.WriteAllBytes(temp, SrtAdCleaner.Encode(file, result.Text));
                var replaced = BackupManager.Replace(path, temp, _backupMode, _backupFolder);
                if (!replaced.Success)
                {
                    System.Threading.Interlocked.Increment(ref Failed);
                    _log.Error($"SubRemover: {path}: {replaced.Message}");
                    return;
                }

                var note = string.Format(BackupNote, result.Removed.Count, result.Removed.Count == 1 ? "" : "s");
                lock (state)
                {
                    if (replaced.NewBackup != null)
                    {
                        replaced.NewBackup.Changes = new List<string> { note };
                        state.Backups.Add(replaced.NewBackup);
                    }
                    else if (replaced.BackupPath != null)
                    {
                        var earlier = state.Backups.LastOrDefault(b => string.Equals(b.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
                        if (earlier != null) earlier.Changes = (earlier.Changes ?? new List<string>()).Concat(new[] { note }).ToList();
                    }
                }
                Note(path, state);
                _log.Info($"SubRemover: removed {result.Removed.Count} ad line{(result.Removed.Count == 1 ? "" : "s")} from {path}" +
                          (replaced.BackupPath != null ? $" (backup: {replaced.BackupPath})" : " (no backup kept)") + ":" + lines);
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref Failed);
                _log.Error($"SubRemover: couldn't check {path} for ads: {ex.Message}");
            }
        }

        private void Note(string path, SubremoverState state)
        {
            if (_countOnly) return;
            var stamp = MovedVideos.StampOrNull(path);
            if (stamp == null) return;
            lock (state) state.AdChecked[path] = stamp;
        }

        /// <summary>"12 files had 31 ad lines removed" for the logs and the settings page.</summary>
        public string Describe() =>
            (_countOnly
                ? $"{FilesWithAds} of {FilesChecked} subtitle files have ads ({CuesRemoved} lines; nothing removed)"
                : $"{CuesRemoved} ad lines removed from {FilesWithAds} of {FilesChecked} subtitle files") +
            (Failed > 0 ? $", {Failed} couldn't be read or changed" : "");

        /// <summary>Subtitle-file backups have their own "Delete ... older than": they're tiny.</summary>
        public static bool IsSubtitleBackup(BackupEntry entry) =>
            string.Equals(Path.GetExtension(entry.OriginalPath), ".srt", StringComparison.OrdinalIgnoreCase);
    }
}
