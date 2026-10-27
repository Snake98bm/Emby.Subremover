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
        private readonly ILogger _log;
        private readonly StateStore _store;

        public RestoreBackupsTask(ILibraryMonitor libraryMonitor, ILogManager logManager, IJsonSerializer json, IApplicationPaths appPaths)
        {
            _libraryMonitor = libraryMonitor;
            _log = logManager.GetLogger("Subremover");
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
        }

        public string Name => "Restore originals from backups";
        public string Key => "SubremoverRestoreBackups";
        public string Description => "Undoes Subremover: puts the backed-up original files back. Honors 'Only process paths containing' so you can restore a single title.";
        public string Category => "Subremover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            var filter = options.OnlyPathContains?.Trim();
            var state = _store.Load();
            var entries = state.Backups
                .Where(b => string.IsNullOrEmpty(filter) || b.OriginalPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            _log.Info("Subremover: restoring {0} backups", entries.Count);
            var restored = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(100.0 * i / entries.Count);

                var entry = entries[i];
                if (!File.Exists(entry.BackupPath))
                {
                    _log.Warn("Subremover: backup {0} is missing, forgetting it", entry.BackupPath);
                    state.Backups.Remove(entry);
                    continue;
                }

                _libraryMonitor.ReportFileSystemChangeBeginning(entry.OriginalPath);
                try
                {
                    BackupManager.Restore(entry);
                    state.Backups.Remove(entry);
                    // Remember the restored file so the next run doesn't immediately strip it again.
                    state.Processed[entry.OriginalPath] = FileStamp.Of(entry.OriginalPath);
                    restored++;
                    _log.Info("Subremover: restored {0}", entry.OriginalPath);
                }
                catch (Exception ex)
                {
                    _log.ErrorException("Subremover: could not restore {0}", ex, entry.OriginalPath);
                }
                finally
                {
                    _libraryMonitor.ReportFileSystemChangeComplete(entry.OriginalPath, true);
                }
                _store.Save(state);
            }

            _store.Save(state);
            progress.Report(100);
            _log.Info("Subremover: restored {0} of {1} backups", restored, entries.Count);
            return Task.CompletedTask;
        }
    }
}
