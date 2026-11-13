using System;
using System.Collections.Generic;
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

        private IScheduledTaskWorker HashTaskWorker() =>
            _applicationHost.Resolve<ITaskManager>().ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is HashSubtitlesTask);

        /// <returns>The status line to show after "Count" or "Download hash-matched subtitles".</returns>
        internal StatusItem StartHashRun(bool countOnly)
        {
            var worker = HashTaskWorker();
            if (worker != null && worker.State != TaskState.Idle) return HashStatus();
            lock (_hashLock) _hashRunMode = countOnly;
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<HashSubtitlesTask>();
            return new StatusItem("Hash-matched subtitles", $"{(countOnly ? "Counting" : "Downloading")} started at {DateTime.Now:HH:mm}. " +
                                                             "The result shows here and in its log.", ItemStatus.InProgress);
        }

        internal StatusItem HashStatus()
        {
            const string caption = "Hash-matched subtitles";
            try
            {
                var worker = HashTaskWorker();
                if (worker?.State == TaskState.Running)
                    return new StatusItem(caption, worker.CurrentProgress.HasValue ? $"Running – {Math.Round(worker.CurrentProgress.Value)}% done" : "Running", ItemStatus.InProgress);
                if (worker?.State == TaskState.Cancelling) return new StatusItem(caption, "Stopping…", ItemStatus.Warning);
                var last = HashSubtitlesTask.LastResult;
                return last == null
                    ? new StatusItem(caption, "Not run since Emby started.", ItemStatus.None)
                    : new StatusItem(caption, last, last.Contains("Stopped") ? ItemStatus.Warning : ItemStatus.Succeeded);
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
                    return new StatusItem(AdCaption, worker.CurrentProgress.HasValue ? $"Running – {Math.Round(worker.CurrentProgress.Value)}% done" : "Running", ItemStatus.InProgress);
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
                    ? new StatusItem(AdCaption, "Not run since Emby started.", ItemStatus.None)
                    : new StatusItem(AdCaption, last, last.StartsWith("Stopped") || last.StartsWith("Didn't") ? ItemStatus.Warning : ItemStatus.Succeeded);
            }
            catch (Exception ex)
            {
                return new StatusItem(AdCaption, "Status unavailable: " + ex.Message, ItemStatus.Unknown);
            }
        }

        // Never let a status problem stop the settings page from opening.
        public const string RestoreCaption = "Restore";

        /// <summary>
        /// Every video with a backup, newest first so what was just changed is at the top, or why the Restore button won't.
        /// The path filter isn't used: the ticks on the list decide what's restored.
        /// </summary>
        internal (string Refusal, List<BackupEntry> Backups) RestorePlan(string show = null)
        {
            var busy = _applicationHost.Resolve<ITaskManager>().ScheduledTasks
                .Any(w => (w.ScheduledTask is RemoveSubtitlesTask || w.ScheduledTask is RestoreBackupsTask || w.ScheduledTask is SubtitleAdsTask)
                          && w.State != TaskState.Idle);
            var backups = NewestFirst(OfKind(_state.Load().Backups, show));
            return (RestoreRefusal(busy, backups.Count, show), backups);
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

        internal string BackupCountText()
        {
            try
            {
                var count = _state.Load().Backups.Count;
                return count == 1 ? "1 video has a backup." : $"{count} videos have a backup.";
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
