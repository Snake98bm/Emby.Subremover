using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Emby.Subremover.Core;
using Emby.Subremover.ScheduledTasks;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover
{
    public class Plugin : BasePlugin, IHasWebPages, IHasThumbImage
    {
        private readonly IApplicationHost _applicationHost;
        private readonly ILibraryManager _libraryManager;
        private readonly OptionsStore _store;
        private readonly StateStore _state;
        private readonly string _logDirectory;

        public Plugin(IApplicationHost applicationHost, ILibraryManager libraryManager)
        {
            _applicationHost = applicationHost;
            _libraryManager = libraryManager;
            _store = new OptionsStore(
                applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath,
                SettingsFileName,
                applicationHost.Resolve<IJsonSerializer>(),
                applicationHost.Resolve<ILogManager>().GetLogger(Name));
            _logDirectory = applicationHost.Resolve<IApplicationPaths>().LogDirectoryPath;
            _state = new StateStore(applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath, applicationHost.Resolve<IJsonSerializer>());
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        // The settings file keeps the name earlier versions used, so saved settings carry over after the rename.
        private const string SettingsFileName = "Subremover";

        /// <summary>"0.6.9", or "0.6.9-alpha.3" for a local build.</summary>
        public static string VersionText
        {
            get
            {
                var informational = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(informational)) return informational.Split('+')[0];
                var v = typeof(Plugin).Assembly.GetName().Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        public override string Name => "SubRemover";

        public override string Description => "Removes unwanted embedded subtitle tracks from video files, with test mode and backups.";

        public override Guid Id => new Guid("82033f83-61d4-48e1-b20a-831041323b14");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream("Emby.Subremover.thumb.png");

        internal const string PageName = "SubRemoverSettings";

        /// <summary>
        /// The settings page (Configuration/settings.html) and its script. It's the page under Plugins and, when
        /// 'Show in the server sidebar menu' is on, also in the sidebar.
        /// </summary>
        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = PageName,
                DisplayName = Name,
                EmbeddedResourcePath = "Emby.Subremover.Configuration.settings.html",
                IsMainConfigPage = true,
                EnableInMainMenu = Options.ShowInSidebar,
                // The sidebar's top "Server" section (next to Dashboard and Libraries), not on its own under Advanced.
                MenuSection = "server",
                MenuIcon = "subtitles_off"
            },
            new PluginPageInfo
            {
                Name = PageName + "Js",
                EmbeddedResourcePath = "Emby.Subremover.Configuration.settings.js"
            }
        };

        public PluginOptions Options => _store.Get();

        internal PluginOptions OptionsForEditing() => _store.GetCopy();

        public void Save(PluginOptions options) => _store.Save(options);

        public override PluginInfo GetPluginInfo()
        {
            var info = base.GetPluginInfo();
            info.ConfigurationFileName = _store.FilePath;
            return info;
        }

        // The task manager is looked up when needed rather than at startup, when not every service is ready yet.
        private IScheduledTaskWorker RemoveTaskWorker() =>
            _applicationHost.Resolve<ITaskManager>().ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is RemoveSubtitlesTask);

        /// <returns>The status line to show after the click.</returns>
        internal StatusItem RunNow()
        {
            var worker = RemoveTaskWorker();
            if (worker != null && worker.State != TaskState.Idle) return CurrentStatus();

            NewVideos.Take(); // a full run checks them anyway
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<RemoveSubtitlesTask>();
            return RunStatus.Started(DateTime.Now);
        }

        // ---- Updates from GitHub releases

        internal UpdateStateStore UpdateStore =>
            new UpdateStateStore(_applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath, _applicationHost.Resolve<IJsonSerializer>());

        /// <summary>
        /// The DLL Emby loaded SubRemover from, which an update replaces: the file of that name in the plugins folder (where
        /// Emby looks at startup), else the file it was loaded from.
        /// </summary>
        internal string PluginFile()
        {
            var pluginsPath = _applicationHost.Resolve<IApplicationPaths>().PluginsPath;
            var location = typeof(Plugin).Assembly.Location;
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(location)) candidates.Add(Path.Combine(pluginsPath, Path.GetFileName(location)));
            candidates.Add(Path.Combine(pluginsPath, Updater.AssetName));
            if (!string.IsNullOrEmpty(location)) candidates.Add(location);
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }

        internal Updater CreateUpdater() => new Updater(
            new GitHubReleaseSource(_applicationHost.Resolve<IJsonSerializer>()),
            UpdateStore,
            PluginVersion.Parse(VersionText),
            PluginFile(),
            Path.Combine(_applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath, "subremover-update"));

        // Set by "Install now": the next update run installs even when installing automatically is off.
        private int _updateInstallClick;

        internal bool TakeUpdateInstallClick() => System.Threading.Interlocked.Exchange(ref _updateInstallClick, 0) == 1;

        private IScheduledTaskWorker UpdateTaskWorker() =>
            _applicationHost.Resolve<ITaskManager>().ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is UpdateTask);

        /// <summary>"Check for updates now" (install = false) or "Install now".</summary>
        internal StatusItem StartUpdate(bool install)
        {
            var worker = UpdateTaskWorker();
            if (worker != null && worker.State != TaskState.Idle) return UpdateStatus().Status;
            if (install) System.Threading.Interlocked.Exchange(ref _updateInstallClick, 1);
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<UpdateTask>();
            return new StatusItem(Updater.Caption, install ? "Installing the newest release…" : "Checking GitHub for a newer release…", ItemStatus.InProgress);
        }

        /// <summary>The update status line, the version an Install now button would install (or null), and the release's page.</summary>
        internal (StatusItem Status, string Available, string ReleaseUrl) UpdateStatus()
        {
            try
            {
                var state = UpdateStore.Load();
                var worker = UpdateTaskWorker();
                if (worker != null && worker.State != TaskState.Idle)
                    return (new StatusItem(Updater.Caption, UpdateTask.Live ?? "Checking for updates…", ItemStatus.InProgress), null, state.ReleaseUrl);
                var (text, status, available) = Updater.Describe(state, PluginVersion.Parse(VersionText), Options.AutoUpdate);
                return (new StatusItem(Updater.Caption, text, status), available, state.ReleaseUrl);
            }
            catch (Exception ex)
            {
                return (new StatusItem(Updater.Caption, "Update status unavailable: " + ex.Message, ItemStatus.Unknown), null, null);
            }
        }

        // ---- Errors tab

        internal const string ErrorsCaption = "Errors";

        // Set by "Retry now": the next run's videos are retries, not new videos (only changes how its log words it).
        private int _retryRun;

        internal bool TakeRetryRun() => System.Threading.Interlocked.Exchange(ref _retryRun, 0) == 1;

        internal FailureLog FailureLog => new FailureLog(_applicationHost.Resolve<IApplicationPaths>().LogDirectoryPath);

        /// <summary>The videos on the Errors tab, most recent failure first.</summary>
        internal List<KeyValuePair<string, FailedVideo>> FailedVideoList() =>
            _state.Load().Failures.OrderByDescending(kv => kv.Value.LastUtc).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// "Retry now" / "Retry on next run": forgets the failures of the chosen videos (only ones on the list: the paths
        /// come from the browser) and, for Retry now, starts a run for just those.
        /// </summary>
        internal StatusItem RetryFailed(IEnumerable<string> paths, bool runNow)
        {
            var worker = RemoveTaskWorker();
            // A running task holds the state in memory and would write the failures back when it saves.
            if ((worker != null && worker.State != TaskState.Idle) || !TaskGate.TryEnter())
                return new StatusItem(ErrorsCaption, "Nothing changed: " + TaskGate.BusyMessage + ".", ItemStatus.Warning);

            List<string> chosen;
            try
            {
                var state = _state.Load();
                chosen = (paths ?? Enumerable.Empty<string>())
                    .Select(p => state.Failures.Keys.FirstOrDefault(k => string.Equals(k, p?.Trim(), StringComparison.OrdinalIgnoreCase)))
                    .Where(p => p != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (chosen.Count == 0)
                    return new StatusItem(ErrorsCaption, "Nothing changed: none of the ticked videos are on the list any more. Click Refresh.", ItemStatus.Warning);
                foreach (var p in chosen) FailedVideos.Forget(state, p);
                _state.Save(state);
            }
            finally
            {
                TaskGate.Exit();
            }

            var log = FailureLog;
            foreach (var p in chosen) log.Write((runNow ? "RETRY    " : "FORGOTTEN ") + p + (runNow ? ": retry started from the Errors tab" : ": failures forgotten; the next run tries it again"));

            var what = chosen.Count == 1 ? "1 video" : chosen.Count + " videos";
            if (!runNow)
                return new StatusItem(ErrorsCaption, $"Forgot the failures of {what}. The next run tries {(chosen.Count == 1 ? "it" : "them")} again.", ItemStatus.Succeeded);

            NewVideos.AddRange(chosen);
            System.Threading.Interlocked.Exchange(ref _retryRun, 1);
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<RemoveSubtitlesTask>();
            return new StatusItem(ErrorsCaption,
                $"Retrying {what}, started at {DateTime.Now:HH:mm}. Click Refresh when it's done; its log is on the Logs tab.", ItemStatus.InProgress);
        }

        // The button pressed for the next "Download hash-matched subtitles" run: true = count only; null = scheduled run.
        private bool? _hashRunMode;

        internal bool? TakeHashRunMode()
        {
            lock (_hashLock)
            {
                var mode = _hashRunMode;
                _hashRunMode = null;
                return mode;
            }
        }

        private readonly object _hashLock = new object();

        // "Start over" was clicked for the next hash run: forget where a stopped one got to.
        private bool _hashStartOver;

        internal bool TakeHashStartOver()
        {
            lock (_hashLock)
            {
                var startOver = _hashStartOver;
                _hashStartOver = false;
                return startOver;
            }
        }

        private IScheduledTaskWorker HashTaskWorker() =>
            _applicationHost.Resolve<ITaskManager>().ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is HashSubtitlesTask);

        internal TaskProgressStore ProgressStore => new TaskProgressStore(_applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath, _applicationHost.Resolve<IJsonSerializer>());

        /// <summary>Where a stopped Count (true) or Download (false) got to, or null.</summary>
        internal TaskProgress StoppedHashRun(bool countOnly) => HashSubtitlesTask.Stopped(ProgressStore, Options, countOnly);

        /// <param name="startOver">Forget where a stopped run of this kind got to; otherwise it carries on from there.</param>
        /// <returns>The status line to show after "Count" or "Download hash-matched subtitles".</returns>
        internal StatusItem StartHashRun(bool countOnly, bool startOver = false)
        {
            var worker = HashTaskWorker();
            if (worker != null && worker.State != TaskState.Idle) return HashStatus();
            var stopped = startOver ? null : StoppedHashRun(countOnly);
            lock (_hashLock)
            {
                _hashRunMode = countOnly;
                _hashStartOver = startOver;
            }
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<HashSubtitlesTask>();
            var what = countOnly ? "Counting" : "Downloading";
            return new StatusItem("Hash-matched subtitles",
                (stopped != null ? $"{what} carries on ({stopped.Describe()})" : $"{what} started") + $" at {DateTime.Now:HH:mm}. " +
                "The result shows here and in its log.", ItemStatus.InProgress);
        }

        /// <summary>"Running – 19% done. Last time: Count finished 16 Nov 05:30: ..." while a task runs.</summary>
        internal static string WithLast(string running, string last) =>
            string.IsNullOrEmpty(last) ? running : running + ". Last time: " + last;

        /// <summary>"Count was stopped (1,234 of 3,026 videos done, stopped 16 Nov 09:40): Count carries on from there
        /// unless Start over is ticked." Null when nothing was stopped.</summary>
        internal static string StoppedNote(params (string Button, TaskProgress Progress)[] runs)
        {
            var stopped = runs.Where(r => r.Progress != null).ToList();
            if (stopped.Count == 0) return null;
            return string.Join(" ", stopped.Select(r =>
                $"{r.Button} was stopped ({r.Progress.Describe()}): {r.Button} carries on from there unless Start over is ticked."));
        }

        internal StatusItem HashStatus()
        {
            const string caption = "Hash-matched subtitles";
            try
            {
                var worker = HashTaskWorker();
                if (worker?.State == TaskState.Running)
                    return new StatusItem(caption, WithLast(worker.CurrentProgress.HasValue ? $"Running – {Math.Round(worker.CurrentProgress.Value)}% done" : "Running", HashSubtitlesTask.LastResult), ItemStatus.InProgress);
                if (worker?.State == TaskState.Cancelling) return new StatusItem(caption, "Stopping…", ItemStatus.Warning);
                var last = HashSubtitlesTask.LastResult;
                var stopped = StoppedNote(("Count", StoppedHashRun(true)), ("Download", StoppedHashRun(false)));
                if (last == null) return new StatusItem(caption, stopped ?? "Not run yet.", stopped != null ? ItemStatus.Warning : ItemStatus.None);
                return new StatusItem(caption, last + (stopped != null ? " " + stopped : ""),
                    last.Contains("Stopped") || stopped != null ? ItemStatus.Warning : ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                return new StatusItem(caption, "Status unavailable: " + ex.Message, ItemStatus.Unknown);
            }
        }

        // The button pressed for the next "Remove ads from subtitle files" run: true = find only; null = scheduled run.
        private bool? _adRunMode;
        private DateTime _adQueuedUtc;
        private readonly object _adLock = new object();

        internal bool? TakeAdRunMode()
        {
            lock (_adLock)
            {
                var mode = _adRunMode;
                _adRunMode = null;
                return mode;
            }
        }

        private IScheduledTaskWorker AdTaskWorker() =>
            _applicationHost.Resolve<ITaskManager>().ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is SubtitleAdsTask);

        internal const string AdCaption = "Ads in subtitle files";

        /// <returns>The status line to show after "Test run" (find ads) or "Remove ads".</returns>
        internal StatusItem StartAdRun(bool findOnly)
        {
            var worker = AdTaskWorker();
            if (worker != null && worker.State != TaskState.Idle) return AdStatus();
            lock (_adLock)
            {
                _adRunMode = findOnly;
                _adQueuedUtc = DateTime.UtcNow;
            }
            // The link would otherwise point at the previous run's log until this one has started.
            SubtitleAdsTask.LastLogName = null;
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<SubtitleAdsTask>();
            return new StatusItem(AdCaption, $"{(findOnly ? "Finding" : "Removing")} started at {DateTime.Now:HH:mm}. " +
                                             "The result shows here and in its log.", ItemStatus.InProgress);
        }

        internal StatusItem AdStatus()
        {
            try
            {
                var worker = AdTaskWorker();
                if (worker?.State == TaskState.Running)
                    return new StatusItem(AdCaption, WithLast(worker.CurrentProgress.HasValue ? $"Running – {Math.Round(worker.CurrentProgress.Value)}% done" : "Running", SubtitleAdsTask.LastResult), ItemStatus.InProgress);
                if (worker?.State == TaskState.Cancelling) return new StatusItem(AdCaption, "Stopping…", ItemStatus.Warning);
                // Queued by a button but not started yet: still in progress, so the page keeps checking.
                bool queued;
                lock (_adLock)
                {
                    // Never started within two minutes: stop saying so (the next scheduled run then follows Test mode).
                    if (_adRunMode != null && DateTime.UtcNow - _adQueuedUtc > TimeSpan.FromMinutes(2)) _adRunMode = null;
                    queued = _adRunMode != null;
                }
                if (queued) return new StatusItem(AdCaption, "Starting…", ItemStatus.InProgress);
                var last = SubtitleAdsTask.LastResult;
                return last == null
                    ? new StatusItem(AdCaption, "Not run yet.", ItemStatus.None)
                    : new StatusItem(AdCaption, last, last.StartsWith("Stopped") || last.StartsWith("Didn't") ? ItemStatus.Warning : ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                return new StatusItem(AdCaption, "Status unavailable: " + ex.Message, ItemStatus.Unknown);
            }
        }

        // The button pressed for the next "Fix subtitle file timing" run: true = scan run; null = scheduled run.
        private bool? _timingRunMode;
        private DateTime _timingQueuedUtc;
        private readonly object _timingLock = new object();

        internal bool? TakeTimingRunMode()
        {
            lock (_timingLock)
            {
                var mode = _timingRunMode;
                _timingRunMode = null;
                return mode;
            }
        }

        private IScheduledTaskWorker TimingTaskWorker() =>
            _applicationHost.Resolve<ITaskManager>().ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is SubtitleTimingTask);

        internal const string TimingCaption = "Subtitle file timing";

        // "Start over" was ticked for the next timing run.
        private bool _timingStartOver;

        internal bool TakeTimingStartOver()
        {
            lock (_timingLock)
            {
                var startOver = _timingStartOver;
                _timingStartOver = false;
                return startOver;
            }
        }

        /// <summary>Where a stopped scan run (true) or fix (false) got to, or null.</summary>
        internal TaskProgress StoppedTimingRun(bool testOnly) => SubtitleTimingTask.Stopped(ProgressStore, Options, testOnly);

        /// <param name="startOver">Forget where a stopped run got to and measure every file again, even ones found good before.</param>
        /// <returns>The status line to show after "Scan run" or "Fix timing now".</returns>
        internal StatusItem StartTimingRun(bool testOnly, bool startOver = false)
        {
            var worker = TimingTaskWorker();
            if (worker != null && worker.State != TaskState.Idle) return TimingStatus();
            var stopped = startOver ? null : StoppedTimingRun(testOnly);
            lock (_timingLock)
            {
                _timingRunMode = testOnly;
                _timingStartOver = startOver;
                _timingQueuedUtc = DateTime.UtcNow;
            }
            SubtitleTimingTask.LastLogName = null;
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<SubtitleTimingTask>();
            var what = testOnly ? "Scan run" : "Fixing";
            return new StatusItem(TimingCaption,
                (stopped != null ? $"{what} carries on ({stopped.Describe()})" : $"{what} started") + $" at {DateTime.Now:HH:mm}. " +
                "The result shows here and in its log.", ItemStatus.InProgress);
        }

        internal StatusItem TimingStatus()
        {
            try
            {
                var worker = TimingTaskWorker();
                if (worker?.State == TaskState.Running)
                    return new StatusItem(TimingCaption, WithLast(worker.CurrentProgress.HasValue ? $"Running – {Math.Round(worker.CurrentProgress.Value)}% done" : "Running", SubtitleTimingTask.LastResult), ItemStatus.InProgress);
                if (worker?.State == TaskState.Cancelling) return new StatusItem(TimingCaption, "Stopping…", ItemStatus.Warning);
                bool queued;
                lock (_timingLock)
                {
                    if (_timingRunMode != null && DateTime.UtcNow - _timingQueuedUtc > TimeSpan.FromMinutes(2)) _timingRunMode = null;
                    queued = _timingRunMode != null;
                }
                if (queued) return new StatusItem(TimingCaption, "Starting…", ItemStatus.InProgress);
                var last = SubtitleTimingTask.LastResult;
                var stopped = StoppedNote(("Scan run", StoppedTimingRun(true)), ("Fix timing now", StoppedTimingRun(false)));
                if (last == null) return new StatusItem(TimingCaption, stopped ?? "Not run yet.", stopped != null ? ItemStatus.Warning : ItemStatus.None);
                return new StatusItem(TimingCaption, last + (stopped != null && !last.Contains(" paused ") ? " " + stopped : ""),
                    last.StartsWith("Stopped") || last.StartsWith("Didn't") || stopped != null ? ItemStatus.Warning : ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                return new StatusItem(TimingCaption, "Status unavailable: " + ex.Message, ItemStatus.Unknown);
            }
        }

        // Never let a status problem stop the settings page from opening.
        public const string RestoreCaption = "Restore";

        /// <summary>
        /// Every video with a backup, newest first so what was just changed is at the top, or why the Restore button won't.
        /// The path filter isn't used: the ticks on the list decide what's restored.
        /// </summary>
        /// <param name="why">A <see cref="Core.BackupReasons"/> value; "any" or empty for every reason.</param>
        /// <returns>Also every backup of this kind before the reason filter, for the reason list's counts.</returns>
        internal (string Refusal, List<BackupEntry> Backups, List<BackupEntry> OfThisKind) RestorePlan(string show = null, string why = null)
        {
            var busy = _applicationHost.Resolve<ITaskManager>().ScheduledTasks
                .Any(w => (w.ScheduledTask is RemoveSubtitlesTask || w.ScheduledTask is RestoreBackupsTask || w.ScheduledTask is SubtitleAdsTask || w.ScheduledTask is SubtitleTimingTask)
                          && w.State != TaskState.Idle);
            var ofKind = OfKind(_state.Load().Backups, show).ToList();
            var backups = NewestFirst(Core.BackupReasons.Filter(ofKind, why));
            var refusal = RestoreRefusal(busy, backups.Count, show);
            // Backups of this kind, just none for that reason: say so, rather than "no backups".
            if (!busy && backups.Count == 0 && ofKind.Count > 0) refusal = "There are no backups made for that reason.";
            return (refusal, backups, ofKind);
        }

        /// <summary>The restore list's "Show": "videos", "subtitles", or anything else for all backups.</summary>
        internal static IEnumerable<BackupEntry> OfKind(IEnumerable<BackupEntry> backups, string show)
        {
            switch ((show ?? "").ToLowerInvariant())
            {
                case "videos": return backups.Where(b => !Core.SubtitleAdFiles.IsSubtitleBackup(b));
                case "subtitles": return backups.Where(Core.SubtitleAdFiles.IsSubtitleBackup);
                default: return backups;
            }
        }

        internal static List<BackupEntry> NewestFirst(IEnumerable<BackupEntry> backups) =>
            backups.OrderByDescending(b => b.CreatedUtc).ThenBy(b => b.OriginalPath, StringComparer.OrdinalIgnoreCase).ToList();

        // New videos Emby added, handed over by NewVideoWatcher: the next run checks only these.
        internal PendingVideos NewVideos { get; } = new PendingVideos();

        // The videos selected on the restore list, for the restore task to take instead of the path filter.
        private List<string> _pendingRestore;

        internal List<string> TakePendingRestore() => System.Threading.Interlocked.Exchange(ref _pendingRestore, null);

        /// <returns>The status line to show under the Restore button.</returns>
        internal StatusItem StartRestore(IList<BackupEntry> chosen)
        {
            _pendingRestore = chosen.Select(b => b.OriginalPath).ToList();
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<RestoreBackupsTask>();
            return new StatusItem(RestoreCaption,
                $"Restoring {chosen.Count} video{(chosen.Count == 1 ? "" : "s")}, started at {DateTime.Now:HH:mm}. Its log is on the Logs tab.", ItemStatus.InProgress);
        }

        /// <summary>
        /// Deletes the chosen backups for good: the backup file, and its line on the restore list. The original stays as it
        /// is now (changed). Runs here, not as a task: deleting files is quick. Only while nothing else is changing the record.
        /// </summary>
        internal StatusItem DeleteBackups(IList<BackupEntry> chosen)
        {
            if (!TaskGate.TryEnter()) return new StatusItem(RestoreCaption, "Nothing deleted: another SubRemover task is going. Try again when it has finished.", ItemStatus.Warning);
            try
            {
                var state = _state.Load();
                int deleted = 0, gone = 0, failed = 0;
                var log = _applicationHost.Resolve<ILogManager>().GetLogger("SubRemover");
                foreach (var backup in chosen)
                {
                    var entry = state.Backups.FirstOrDefault(b => string.Equals(b.BackupPath, backup.BackupPath, StringComparison.OrdinalIgnoreCase)
                                                                  && string.Equals(b.OriginalPath, backup.OriginalPath, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) continue;
                    try
                    {
                        if (File.Exists(entry.BackupPath)) { File.Delete(entry.BackupPath); deleted++; }
                        else gone++;
                        state.Backups.Remove(entry);
                        log.Info("SubRemover: deleted the backup {0} of {1} (asked for on the restore list)", entry.BackupPath, entry.OriginalPath);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        log.Error("SubRemover: couldn't delete the backup {0}: {1}", entry.BackupPath, ex.Message);
                    }
                }
                _state.Save(state);
                var text = $"Deleted {deleted} backup{(deleted == 1 ? "" : "s")}" +
                           (gone > 0 ? $"; {gone} {(gone == 1 ? "was" : "were")} already gone and came off the list" : "") +
                           (failed > 0 ? $"; {failed} couldn't be deleted (see Emby's log)" : "") + ". Those files can't be restored now.";
                return new StatusItem(RestoreCaption, text, failed > 0 ? ItemStatus.Warning : ItemStatus.Succeeded);
            }
            finally
            {
                TaskGate.Exit();
            }
        }

        /// <summary>The word to type before backups are deleted; any capitals.</summary>
        internal static bool IsDeleteConfirmed(string typed) => string.Equals((typed ?? "").Trim(), "delete", StringComparison.OrdinalIgnoreCase);

        public const int RestoreListLimit = 200;

        /// <summary>The listed videos that are selected, in list order.</summary>
        internal static List<BackupEntry> SelectedBackups(IList<BackupEntry> listed, IEnumerable<int> selection) =>
            (selection ?? Enumerable.Empty<int>())
                .Where(i => i >= 0 && i < Math.Min(listed.Count, RestoreListLimit))
                .Distinct()
                .OrderBy(i => i)
                .Select(i => listed[i])
                .ToList();

        internal static string RestoreRefusal(bool busy, int backups, string show = null)
        {
            if (busy) return "Nothing restored: SubRemover is running. Try again when it has finished.";
            if (backups == 0)
            {
                switch ((show ?? "").ToLowerInvariant())
                {
                    case "videos": return "There are no video backups to restore.";
                    case "subtitles": return "There are no subtitle file backups to restore.";
                    default: return "There are no backups to restore.";
                }
            }
            return null;
        }

        internal StatusItem CurrentStatus()
        {
            try
            {
                var worker = RemoveTaskWorker();
                return RunStatus.Describe(worker?.State, worker?.CurrentProgress, worker?.LastExecutionResult, _state.Load().LastRun);
            }
            catch (Exception ex)
            {
                return new StatusItem("Status", "Status unavailable: " + ex.Message, ItemStatus.Unknown);
            }
        }

        /// <summary>"1,153 videos and 49 subtitle files have a backup."</summary>
        internal static string BackupCountText(int videos, int subtitles)
        {
            string Part(int n, string one, string many) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
            if (videos == 0 && subtitles == 0) return "No backups.";
            var parts = new List<string>();
            if (videos > 0) parts.Add(Part(videos, "video", "videos"));
            if (subtitles > 0) parts.Add(Part(subtitles, "subtitle file", "subtitle files"));
            return string.Join(" and ", parts) + (videos + subtitles == 1 ? " has a backup." : " have a backup.");
        }

        internal string BackupCountText()
        {
            try
            {
                var backups = _state.Load().Backups;
                var subtitles = backups.Count(Core.SubtitleAdFiles.IsSubtitleBackup);
                return BackupCountText(backups.Count - subtitles, subtitles);
            }
            catch (Exception ex)
            {
                return "Backups unavailable: " + ex.Message;
            }
        }

        /// <summary>The libraries that can be picked: everything but music, books and photos.</summary>
        internal List<EditorSelectOption> LibraryChoices() =>
            _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType != "music" && f.CollectionType != "books" && f.CollectionType != "photos")
                .Select(f => new EditorSelectOption { Value = f.ItemId, Name = f.Name, IsEnabled = true })
                .ToList();

        /// <summary>The secret in the log links on the settings page.</summary>
        internal LogLinkKey LogLinkKey { get; } = new LogLinkKey();

        // Only the kept languages: the default can't go to a subtitle that gets removed.
        internal static List<EditorSelectOption> DefaultLanguageChoicesFor(string keepLanguages)
        {
            var choices = new List<EditorSelectOption> { new EditorSelectOption { Value = string.Empty, Name = "Automatic", IsEnabled = true } };
            choices.AddRange(PlannerSettings.ParseLanguages(keepLanguages)
                .Where(code => code != "und")
                .OrderBy(Languages.NameOf, StringComparer.OrdinalIgnoreCase)
                .Select(code => new EditorSelectOption { Value = code, Name = $"{Languages.NameOf(code)} ({code})", IsEnabled = true }));
            return choices;
        }

        // Codes saved earlier that aren't in the standard list stay selectable, so saving never drops them.
        internal static List<EditorSelectOption> LanguageChoicesFor(string selected)
        {
            var standard = Languages.All.Select(l => new EditorSelectOption { Value = l.Code, Name = $"{l.Name} ({l.Code})", IsEnabled = true });
            var extra = PlannerSettings.ParseLanguages(selected)
                .Where(code => Languages.All.All(l => l.Code != code))
                .Select(code => new EditorSelectOption { Value = code, Name = code, IsEnabled = true });
            return standard.Concat(extra).ToList();
        }
    }
}
