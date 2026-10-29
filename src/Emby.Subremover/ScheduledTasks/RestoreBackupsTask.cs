using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover.ScheduledTasks
{
    public class RestoreBackupsTask : IScheduledTask
    {
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly ILogger _embyLog;
        private readonly string _logDirectory;
        private readonly StateStore _store;
        private ILogger _log;

        public RestoreBackupsTask(ILibraryMonitor libraryMonitor, ILogManager logManager, IJsonSerializer json, IApplicationPaths appPaths)
        {
            _libraryMonitor = libraryMonitor;
            _embyLog = logManager.GetLogger("SubRemover");
            _log = _embyLog;
            _logDirectory = appPaths.LogDirectoryPath;
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
        }

        public string Name => "Restore originals from backups";
        public string Key => "SubremoverRestoreBackups";
        public string Description => "Undoes SubRemover: puts the backed-up original files back. Honors 'Only process paths containing' so you can restore a single title.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            using (var runLog = new RunLog(_logDirectory, "subremover-restore", _embyLog))
            {
                if (!TaskGate.TryEnter())
                {
                    runLog.Summary("SubRemover: the remove task is running - try the restore again when it has finished");
                    return Task.CompletedTask;
                }

                _log = runLog;
                try
                {
                    Run(runLog, cancellationToken, progress);
                }
                finally
                {
                    _log = _embyLog;
                    TaskGate.Exit();
                }
            }
            return Task.CompletedTask;
        }

        /// <summary>The backups whose video path matches <paramref name="filter"/> (see <see cref="PathFilter"/>); all of them when it's empty.</summary>
        internal static List<BackupEntry> Matching(IEnumerable<BackupEntry> backups, string filter) =>
            backups.Where(b => PathFilter.Matches(b.OriginalPath, filter)).ToList();

        private void Run(RunLog runLog, CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            var filter = options.OnlyPathContains?.Trim();
            var state = _store.Load();
            if (state.InProgress != null)
            {
                foreach (var file in Leftovers.Clean(state.InProgress)) _log.Info("SubRemover: deleted leftover {0} from an interrupted run", file);
                state.InProgress = null;
                _store.Save(state);
            }

            // Started from the Restore button: exactly the videos left ticked there. From Scheduled Tasks: the path filter.
            var ticked = Plugin.Instance?.TakePendingRestore();
            var entries = ticked != null
                ? state.Backups.Where(b => ticked.Contains(b.OriginalPath, StringComparer.OrdinalIgnoreCase)).ToList()
                : Matching(state.Backups, filter);

            runLog.Summary("SubRemover: restoring {0} backups. Details: {1}", entries.Count, runLog.FileName);
            var restored = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(100.0 * i / entries.Count);

                var entry = entries[i];
                if (!File.Exists(entry.BackupPath))
                {
                    _log.Warn("SubRemover: backup {0} is missing, forgetting it", entry.BackupPath);
                    state.Backups.Remove(entry);
                    continue;
                }

                _libraryMonitor.ReportFileSystemChangeBeginning(entry.OriginalPath);
                state.InProgress = entry.OriginalPath;
                _store.Save(state);
                try
                {
                    BackupManager.Restore(entry);
                    state.Backups.Remove(entry);
                    // Restored on purpose: later runs leave it alone, even after settings change.
                    state.MarkRestored(entry.OriginalPath);
                    restored++;
                    _log.Info("SubRemover: restored {0}", entry.OriginalPath);
                }
                catch (Exception ex)
                {
                    _log.ErrorException("SubRemover: could not restore {0}", ex, entry.OriginalPath);
                }
                finally
                {
                    state.InProgress = null;
                    _libraryMonitor.ReportFileSystemChangeComplete(entry.OriginalPath, true);
                }
                _store.Save(state);
            }

            _store.Save(state);
            progress.Report(100);
            runLog.Summary("SubRemover: restored {0} of {1} backups. Details: {2}", restored, entries.Count, runLog.FileName);
        }
    }
}
