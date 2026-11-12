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
using MediaBrowser.Controller.Subtitles;
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
        private readonly SearchOcrStore _searchOcr;
        private readonly CheckpointStore _checkpoints;
        private readonly Func<ILogger, MediaProcessor> _newProcessor;
        private ILogger _log;

        public RemoveSubtitlesTask(ILibraryManager libraryManager, ILogManager logManager, IFfmpegManager ffmpegManager,
            ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem,
            IJsonSerializer json, IApplicationPaths appPaths, ISubtitleManager subtitleManager)
        {
            _libraryManager = libraryManager;
            _embyLog = logManager.GetLogger("SubRemover");
            _log = _embyLog;
            _logDirectory = appPaths.LogDirectoryPath;
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
            var runner = FfmpegRunner.FromEmby(ffmpegManager);
            var tessdata = new Tessdata(Path.Combine(appPaths.DataPath, "subremover"));
            _searchOcr = new SearchOcrStore(appPaths.PluginConfigurationsPath, json);
            _checkpoints = new CheckpointStore(appPaths.PluginConfigurationsPath, json);
            var hashes = new OriginalHashStore(appPaths.PluginConfigurationsPath, json);
            _newProcessor = log => new MediaProcessor(runner, libraryMonitor, providerManager, fileSystem, json, log, tessdata, _searchOcr,
                l => new HashMatchedSubtitles(subtitleManager, hashes, l, "SubRemover"));
        }

        public string Name => "Remove unwanted subtitles";
        public string Key => "SubremoverRemoveSubtitles";
        public string Description => "Removes embedded subtitle tracks that aren't in your kept languages. Configure under Plugins → SubRemover.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            // Set when NewVideoWatcher started this run: check only the videos Emby just added.
            var newVideos = Plugin.Instance.NewVideos.Take();

            using (var runLog = new RunLog(_logDirectory, "subremover", _embyLog))
            {
                if (!TaskGate.TryEnter())
                {
                    const string busy = "another SubRemover task (restore, or removing ads from subtitle files) is running - try again when it has finished";
                    // Hand the new videos back, so the next run still gets to them.
                    Plugin.Instance.NewVideos.AddRange(newVideos);
                    runLog.Summary("SubRemover: {0}", busy);
                    var state = _store.Load();
                    state.LastRun = new RunSummary { FinishedUtc = DateTime.UtcNow, Problem = busy, LogFile = runLog.FileName };
                    _store.Save(state);
                    return;
                }

                _log = runLog;
                try
                {
                    await Run(runLog, newVideos, cancellationToken, progress).ConfigureAwait(false);
                }
                finally
                {
                    _log = _embyLog;
                    TaskGate.Exit();
                }
            }
        }

        private async Task Run(RunLog runLog, ISet<string> newVideos, CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            var processor = _newProcessor(runLog);
            var version = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            runLog.Summary("SubRemover {0} starting{1}{2}. Details: {3}", version,
                newVideos == null ? "" : " for new videos added to the library",
                options.DryRun ? " in TEST MODE (no files will be changed)" : "", runLog.FileName);
            // One pre-built string: values such as paths may contain braces.
            _log.Info("SubRemover settings for this run:\n    " + string.Join("\n    ", options.DescribeSettings(LibraryNames())));

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

            // Test mode checks the videos a real run would check now, without forgetting anything.
            var history = options.DryRun ? state.PreviewFor(signature, options.ReprocessAll) : state;
            if (options.DryRun && options.ReprocessAll) _log.Info("SubRemover: 'Re-check every video' is on - test mode includes videos already done or restored");

            var items = FindVideos(options, newVideos);
            if (newVideos != null)
            {
                _log.Info("SubRemover: {0} new video{1} added; {2} of them in the selected libraries and path filter",
                    newVideos.Count, newVideos.Count == 1 ? "" : "s", items.Count);
            }
            _log.Info("SubRemover: {0} video files to check", items.Count);
            FollowMovedVideos(history, items, options.DryRun);

            var counts = Enum.GetValues(typeof(Outcome)).Cast<Outcome>().ToDictionary(o => o, o => 0);
            var limit = LimitFor(options, newVideosOnly: newVideos != null);
            var changes = 0;

            // "Continue an interrupted run where it stopped". Runs for new videos do their own short list.
            RunCheckpoint checkpoint = null;
            var total = items.Count;
            if (newVideos == null && options.ResumeInterruptedRuns)
            {
                var key = RunCheckpoint.KeyFor(signature, options.DryRun, options.Libraries, options.OnlyPathContains);
                checkpoint = _checkpoints.Load();
                if (checkpoint != null && (checkpoint.Key != key || checkpoint.DryRun != options.DryRun || options.ReprocessAll))
                {
                    _log.Info("SubRemover: an earlier run was interrupted, but the settings have changed since, so this run starts over");
                    checkpoint = null;
                }
                if (checkpoint != null)
                {
                    var done = new HashSet<string>(checkpoint.Checked, StringComparer.OrdinalIgnoreCase);
                    items = items.Where(i => !done.Contains(i.Path)).ToList();
                    foreach (var kv in checkpoint.Counts)
                        if (Enum.TryParse(kv.Key, out Outcome o)) counts[o] = kv.Value;
                    changes = counts[Outcome.Changed] + counts[Outcome.WouldChange];
                    processor.Totals.CopyFrom(checkpoint.Totals);
                    _log.Info($"SubRemover: continuing the run interrupted at {checkpoint.StartedUtc.ToLocalTime():d MMM HH:mm}: {done.Count} of {total} videos " +
                              $"were already checked then (their details are in {checkpoint.LogFile}); {items.Count} to go. The totals below include them.");
                }
                else
                {
                    checkpoint = new RunCheckpoint { Key = key, DryRun = options.DryRun, StartedUtc = DateTime.UtcNow, LogFile = runLog.FileName };
                }
            }

            var checkedBefore = total - items.Count;
            var checkedVideos = new List<string>();
            var lastSaved = DateTime.UtcNow;
            void SaveCheckpoint()
            {
                if (checkpoint == null) return;
                checkpoint.Counts = counts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                lock (processor.Totals) checkpoint.Totals = processor.Totals.Copy();
                try { _checkpoints.Save(checkpoint); } catch (Exception ex) { _log.Debug($"SubRemover: couldn't note the run's progress: {ex.Message}"); }
                lastSaved = DateTime.UtcNow;
            }

            // Several videos are checked at once (low priority, "Videos to check at once"); changes happen one at a time,
            // in order, here.
            var atOnce = Math.Max(1, Math.Min(8, options.VideosAtOnce));
            using (var stopChecks = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var ahead = new Queue<(BaseItem Item, Task<MediaProcessor.Checked> Check)>();
                var next = 0;
                void CheckAhead()
                {
                    while (ahead.Count < atOnce && next < items.Count)
                    {
                        var item = items[next++];
                        ahead.Enqueue((item, Task.Run(() => processor.Check(item, options, history, _store, stopChecks.Token))));
                    }
                }

                try
                {
                    CheckAhead();
                    var position = 0;
                    while (ahead.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress.Report(100.0 * (checkedBefore + position) / Math.Max(1, total));
                        var (item, check) = ahead.Dequeue();
                        position++;

                        Outcome outcome;
                        try
                        {
                            var video = await check.ConfigureAwait(false);
                            CheckAhead();
                            outcome = await processor.Change(video, options, history, _store, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            CheckAhead();
                            _log.ErrorException("SubRemover: unexpected error on {0}", ex, item.Path);
                            outcome = Outcome.Failed;
                        }
                        counts[outcome]++;
                        checkedVideos.Add(item.Path);
                        checkpoint?.Checked.Add(item.Path);
                        if (DateTime.UtcNow - lastSaved > TimeSpan.FromSeconds(30)) SaveCheckpoint();

                        if (outcome == Outcome.Changed || outcome == Outcome.WouldChange) changes++;
                        if (limit > 0 && changes >= limit)
                        {
                            _log.Info("SubRemover: reached the limit of {0} videos per run", limit);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped: keep where it got to for next time.
                    SaveCheckpoint();
                    throw;
                }
                finally
                {
                    stopChecks.Cancel();
                    foreach (var pending in ahead)
                    {
                        try { await pending.Check.ConfigureAwait(false); } catch (Exception) { }
                    }
                }
            }
            // Finished (or stopped at the limit): nothing to continue.
            _checkpoints.Delete();

            if (options.RemoveSubtitleAds) RemoveSubtitleAds(options, state, checkedVideos, cancellationToken);

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
            _log.Info(processor.Totals.Describe(options.DryRun));
        }

        /// <returns>How many videos a run may change; 0 is no limit. A run for new videos does all of them.</returns>
        internal static int LimitFor(PluginOptions options, bool newVideosOnly) => newVideosOnly ? 0 : options.MaxItemsPerRun;

        private static string Validate(PluginOptions o)
        {
            if (string.IsNullOrWhiteSpace(o.Libraries)) return "no libraries selected";
            if (PlannerSettings.ParseLanguages(o.KeepLanguages).Count == 0) return "no languages to keep - refusing to remove every subtitle";
            if (o.BackupMode == BackupMode.BackupFolder && !Directory.Exists(o.BackupFolder)) return "backup folder '" + o.BackupFolder + "' does not exist";
            return null;
        }

        private Dictionary<string, string> LibraryNames() =>
            _libraryManager.GetVirtualFolders()
                .Where(f => f.ItemId != null)
                .GroupBy(f => f.ItemId)
                .ToDictionary(g => g.Key, g => g.First().Name);

        /// <param name="onlyThese">New videos only: still limited to the libraries and path filter.</param>
        private List<BaseItem> FindVideos(PluginOptions options, ISet<string> onlyThese) => FindVideos(_libraryManager, options, onlyThese, _log);

        /// <summary>The videos in the selected libraries and path filter that SubRemover can rewrite.</summary>
        internal static List<BaseItem> FindVideos(ILibraryManager libraryManager, PluginOptions options, ISet<string> onlyThese, ILogger log)
        {
            var selected = new HashSet<string>(options.Libraries.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            var folders = libraryManager.GetVirtualFolders().Where(f => selected.Contains(f.ItemId)).ToList();
            // The settings list only has the libraries' ids; this names them. One pre-built string: paths may contain braces.
            log.Info("SubRemover libraries: " + (folders.Count == 0 ? "none found"
                : string.Join(", ", folders.Select(f => $"{f.Name} ({string.Join("; ", f.Locations ?? Array.Empty<string>())})"))));
            var locations = folders.SelectMany(f => f.Locations ?? Array.Empty<string>()).Select(WithTrailingSeparator).ToArray();

            var items = locations.Length == 0
                ? Array.Empty<BaseItem>()
                : libraryManager.GetItemList(new InternalItemsQuery
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
                    items = libraryManager.GetItemList(new InternalItemsQuery
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
                .Where(i => PathFilter.Matches(i.Path, options.OnlyPathContains))
                // Before File.Exists, so a run for a few new videos doesn't touch every file on the share.
                .Where(i => onlyThese == null || onlyThese.Contains(i.Path))
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

        // Videos moved on the share keep their "already done" record, backup, "restored" mark and the subtitle files made
        // for them. In test mode only the preview copy changes.
        private void FollowMovedVideos(SubremoverState state, IList<BaseItem> items, bool dryRun)
        {
            var moves = MovedVideos.Reconnect(state, items.Select(i => i.Path).ToList(), File.Exists, MovedVideos.StampOrNull);
            if (moves.Count == 0) return;
            if (!dryRun)
            {
                _store.Save(state);
                _searchOcr.ApplyMoves(moves);
            }
            // One pre-built string: paths may contain braces.
            _log.Info($"SubRemover: {moves.Count} video{(moves.Count == 1 ? "" : "s")} moved since an earlier run" +
                      (dryRun ? " (test mode: a real run would note this)" : "") + "; their records and backups now follow them:" +
                      string.Concat(moves.Take(10).Select(m => $"\n    {m.From} -> {m.To}")) +
                      (moves.Count > 10 ? $"\n    and {moves.Count - 10} more" : ""));
        }

        // "Remove ads from subtitle files": the .srt files of the videos this run checked, only those new or changed since
        // they were last checked. Test mode only logs what would go.
        private void RemoveSubtitleAds(PluginOptions options, SubremoverState state, IEnumerable<string> videos, CancellationToken ct)
        {
            var rules = options.AdRules();
            foreach (var problem in rules.Problems) _log.Warn($"SubRemover: 'Text to remove' (subtitle files): {problem}");
            var files = new SubtitleAdFiles(rules, options.DryRun, options.BackupMode, options.BackupFolder, _log);
            files.Begin(state);
            var looked = 0;
            foreach (var video in videos)
            {
                ct.ThrowIfCancellationRequested();
                List<string> srts;
                try { srts = SubtitleAdFiles.FilesFor(video); }
                catch (Exception ex) { _log.Debug($"SubRemover: couldn't list the subtitle files of {video}: {ex.Message}"); continue; }
                foreach (var srt in srts.Where(s => files.NeedsCheck(s, state)))
                {
                    looked++;
                    files.Clean(srt, video, state);
                }
            }
            if (looked > 0 || files.Failed > 0)
                _log.Info("SubRemover: ads in subtitle files" + (options.DryRun ? " (test mode)" : "") + ": " + files.Describe());
            if (!options.DryRun) _store.Save(state);
        }

        private void CleanOldBackups(PluginOptions options, SubremoverState state)
        {
            // Subtitle-file backups (ads removed) have their own, usually longer, time: they're tiny.
            int DaysFor(BackupEntry b) => SubtitleAdFiles.IsSubtitleBackup(b) ? options.SubtitleBackupRetentionDays : options.BackupRetentionDays;
            var now = DateTime.UtcNow;
            var old = state.Backups.Where(b => DaysFor(b) > 0 && b.CreatedUtc < now.AddDays(-DaysFor(b))).ToList();
            if (old.Count == 0) return;

            foreach (var entry in old)
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
                    _log.Info("SubRemover: deleted backup older than {0} days: {1}", DaysFor(entry), entry.BackupPath);
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
