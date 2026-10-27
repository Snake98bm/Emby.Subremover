using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover.ScheduledTasks
{
    public class RemoveSubtitlesTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _embyLog;
        private readonly string _logDirectory;
        private readonly StateStore _store;
        private readonly Func<ILogger, MediaProcessor> _newProcessor;
        private ILogger _log;

        public RemoveSubtitlesTask(ILibraryManager libraryManager, ILogManager logManager, IFfmpegManager ffmpegManager,
            ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem,
            IJsonSerializer json, IApplicationPaths appPaths)
        {
            _libraryManager = libraryManager;
            _embyLog = logManager.GetLogger("SubRemover");
            _log = _embyLog;
            _logDirectory = appPaths.LogDirectoryPath;
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
            var runner = FfmpegRunner.FromEmby(ffmpegManager);
            var tessdata = new Tessdata(Path.Combine(appPaths.DataPath, "subremover"));
            _newProcessor = log => new MediaProcessor(runner, libraryMonitor, providerManager, fileSystem, json, log, tessdata);
        }

        public string Name => "Remove unwanted subtitles";
        public string Key => "SubremoverRemoveSubtitles";
        public string Description => "Removes embedded subtitle tracks that aren't in your kept languages. Configure under Plugins → SubRemover.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            using (var runLog = new RunLog(_logDirectory, "subremover", _embyLog))
            {
                if (!TaskGate.TryEnter())
                {
                    const string busy = "another SubRemover task (restore) is running - try again when it has finished";
                    runLog.Summary("SubRemover: {0}", busy);
                    var state = _store.Load();
                    state.LastRun = new RunSummary { FinishedUtc = DateTime.UtcNow, Problem = busy, LogFile = runLog.FileName };
                    _store.Save(state);
                    return;
                }

                _log = runLog;
                try
                {
                    await Run(runLog, cancellationToken, progress).ConfigureAwait(false);
                }
                finally
                {
                    _log = _embyLog;
                    TaskGate.Exit();
                }
            }
        }

        private async Task Run(RunLog runLog, CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            var processor = _newProcessor(runLog);
            var version = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            runLog.Summary("SubRemover {0} starting{1}. Details: {2}", version, options.DryRun ? " in TEST MODE (no files will be changed)" : "", runLog.FileName);

            var problem = Validate(options);
            if (problem != null)
            {
                _log.Error("SubRemover: {0}. Fix it under Plugins → SubRemover.", problem);
                var failedState = _store.Load();
                failedState.LastRun = new RunSummary { FinishedUtc = DateTime.UtcNow, DryRun = options.DryRun, Problem = problem, LogFile = runLog.FileName };
                _store.Save(failedState);
                return;
            }

            var state = _store.Load();
            if (state.InProgress != null)
            {
                foreach (var file in Leftovers.Clean(state.InProgress)) _log.Info("SubRemover: deleted leftover {0} from an interrupted run", file);
                state.InProgress = null;
                _store.Save(state);
            }

            if (options.ReprocessAll && !options.DryRun)
            {
                _log.Info("SubRemover: 'Re-check every video' is on - clearing the processed and restored lists");
                state.ForgetEverything();
                _store.Save(state);
                options.ReprocessAll = false;
                Plugin.Instance.Save(options);
            }

            var signature = MediaProcessor.SettingsFrom(options).Signature();
            if (!options.DryRun && state.ApplySettingsSignature(signature))
            {
                _log.Info("SubRemover: settings changed since the last run - checking every video again (restored videos stay untouched)");
            }
            if (!options.DryRun) _store.Save(state);

            var items = FindVideos(options);
            _log.Info("SubRemover: {0} video files to check", items.Count);

            var counts = Enum.GetValues(typeof(Outcome)).Cast<Outcome>().ToDictionary(o => o, o => 0);
            var changes = 0;
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(100.0 * i / items.Count);

                Outcome outcome;
                try
                {
                    outcome = await processor.Process(items[i], options, state, _store, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.ErrorException("SubRemover: unexpected error on {0}", ex, items[i].Path);
                    outcome = Outcome.Failed;
                }
                counts[outcome]++;

                if (outcome == Outcome.Changed || outcome == Outcome.WouldChange) changes++;
                if (options.MaxItemsPerRun > 0 && changes >= options.MaxItemsPerRun)
                {
                    _log.Info("SubRemover: reached the limit of {0} videos per run", options.MaxItemsPerRun);
                    break;
                }
            }

            CleanOldBackups(options, state);

            state.LastRun = new RunSummary
            {
                FinishedUtc = DateTime.UtcNow,
                DryRun = options.DryRun,
                Changed = counts[Outcome.Changed],
                WouldChange = counts[Outcome.WouldChange],
                NothingToDo = counts[Outcome.NothingToDo],
                AlreadyDone = counts[Outcome.AlreadyDone],
                Failed = counts[Outcome.Failed],
                LogFile = runLog.FileName
            };
            _store.Save(state);

            progress.Report(100);
            runLog.Summary("SubRemover finished: {0} changed, {1} would change (test mode), {2} nothing to do, {3} already done, {4} failed. Details: {5}",
                counts[Outcome.Changed], counts[Outcome.WouldChange], counts[Outcome.NothingToDo], counts[Outcome.AlreadyDone], counts[Outcome.Failed], runLog.FileName);
        }

        private static string Validate(PluginOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.Libraries)) return "no libraries selected";
            if (PlannerSettings.ParseLanguages(o.KeepLanguages).Count == 0) return "no languages to keep - refusing to remove every subtitle";
            if (o.BackupMode == BackupMode.BackupFolder && !Directory.Exists(o.BackupFolder)) return "backup folder '" + o.BackupFolder + "' does not exist";
            return null;
        }

        private List<BaseItem> FindVideos(PluginOptions options)
        {
            var selected = new HashSet<string>(options.Libraries.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            var folders = _libraryManager.GetVirtualFolders().Where(f => selected.Contains(f.ItemId)).ToList();
            var locations = folders.SelectMany(f => f.Locations ?? Array.Empty<string>()).Select(WithTrailingSeparator).ToArray();

            var items = locations.Length == 0
                ? Array.Empty<BaseItem>()
                : _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    MediaTypes = new[] { "Video" },
                    IsVirtualItem = false,
                    HasPath = true,
                    PathStartsWithAny = locations
                });

            if (items.Length == 0)
            {
                var ids = folders.Select(f => long.TryParse(f.ItemId, out var id) ? id : 0).Where(id => id != 0).ToArray();
                if (ids.Length > 0)
                {
                    items = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        Recursive = true,
                        MediaTypes = new[] { "Video" },
                        IsVirtualItem = false,
                        AncestorIds = ids
                    });
                }
            }

            return items
                .Where(i => !i.IsShortcut && !string.IsNullOrEmpty(i.Path))
                .Where(i => string.IsNullOrWhiteSpace(options.OnlyPathContains)
                            || i.Path.IndexOf(options.OnlyPathContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(i => FfmpegArgs.MuxerFor(i.Path) != null && File.Exists(i.Path))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string WithTrailingSeparator(string path)
        {
            if (path.EndsWith("/") || path.EndsWith("\\")) return path;
            return path + (path.Contains("/") ? "/" : "\\");
        }

        private void CleanOldBackups(PluginOptions options, SubremoverState state)
        {
            if (options.BackupRetentionDays <= 0) return;

            var cutoff = DateTime.UtcNow.AddDays(-options.BackupRetentionDays);
            foreach (var entry in state.Backups.Where(b => b.CreatedUtc < cutoff).ToList())
            {
                if (options.DryRun)
                {
                    _log.Info("SubRemover [TEST MODE]: would delete old backup {0}", entry.BackupPath);
                    continue;
                }
                try
                {
                    if (File.Exists(entry.BackupPath)) File.Delete(entry.BackupPath);
                    state.Backups.Remove(entry);
                    _log.Info("SubRemover: deleted backup older than {0} days: {1}", options.BackupRetentionDays, entry.BackupPath);
                }
                catch (Exception ex)
                {
                    _log.ErrorException("SubRemover: could not delete old backup {0}", ex, entry.BackupPath);
                }
            }
            if (!options.DryRun) _store.Save(state);
        }
    }
}
