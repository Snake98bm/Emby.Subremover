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
    public class Plugin : BasePlugin, IHasUIPages, IHasThumbImage
    {
        private readonly IApplicationHost _applicationHost;
        private readonly ILibraryManager _libraryManager;
        private readonly OptionsStore _store;
        private readonly StateStore _state;
        private readonly string _logDirectory;
        private readonly List<IPluginUIPageController> _pages;

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
            _pages = new List<IPluginUIPageController>
            {
                SettingsPageController.ForPluginList(this, Name),
                SettingsPageController.ForSidebar(this, Name, () => Options.ShowInSidebar)
            };
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

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers => _pages.AsReadOnly();

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

            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<RemoveSubtitlesTask>();
            return RunStatus.Started(DateTime.Now);
        }

        // Never let a status problem stop the settings page from opening.
        public const string RestoreCaption = "Restore";

        /// <summary>The restore list last shown on the settings page, waiting for its confirming click.</summary>
        internal RestoreConfirmation RestoreConfirmation { get; } = new RestoreConfirmation();

        /// <summary>
        /// Every video with a backup, newest first so what was just changed is at the top, or why the Restore button won't.
        /// The path filter isn't used: the ticks on the list decide what's restored.
        /// </summary>
        internal (string Refusal, List<BackupEntry> Backups) RestorePlan()
        {
            var busy = _applicationHost.Resolve<ITaskManager>().ScheduledTasks
                .Any(w => (w.ScheduledTask is RemoveSubtitlesTask || w.ScheduledTask is RestoreBackupsTask) && w.State != TaskState.Idle);
            var backups = NewestFirst(_state.Load().Backups);
            return (RestoreRefusal(busy, backups.Count), backups);
        }

        internal static List<BackupEntry> NewestFirst(IEnumerable<BackupEntry> backups) =>
            backups.OrderByDescending(b => b.CreatedUtc).ThenBy(b => b.OriginalPath, StringComparer.OrdinalIgnoreCase).ToList();

        // The videos selected on the restore list, for the restore task to take instead of the path filter.
        private List<string> _pendingRestore;

        internal List<string> TakePendingRestore() => System.Threading.Interlocked.Exchange(ref _pendingRestore, null);

        /// <returns>The status line to show under the Restore button.</returns>
        internal StatusItem StartRestore(IList<BackupEntry> chosen)
        {
            _pendingRestore = chosen.Select(b => b.OriginalPath).ToList();
            _applicationHost.Resolve<ITaskManager>().QueueIfNotRunning<RestoreBackupsTask>();
            return new StatusItem(RestoreCaption,
                $"Restoring {chosen.Count} video{(chosen.Count == 1 ? "" : "s")}, started at {DateTime.Now:HH:mm}. Its log is under Log files below.", ItemStatus.InProgress);
        }

        public const int RestoreListLimit = 200;

        /// <summary>
        /// One row per video: a box on the left showing whether it's selected, the file name, its folder and when the
        /// backup was made underneath, and a toggle to select or unselect it.
        /// </summary>
        internal static GenericItemList RestoreItemsFor(IList<BackupEntry> backups, Func<int, bool> isSelected) =>
            new GenericItemList(backups.Take(RestoreListLimit).Select((b, i) =>
            {
                var selected = isSelected(i);
                return new GenericListItem
                {
                    IconMode = ItemListIconMode.NoIcons,
                    PrimaryText = Path.GetFileName(b.OriginalPath),
                    SecondaryText = Path.GetDirectoryName(b.OriginalPath) + " - backup made " + b.CreatedUtc.ToLocalTime().ToString("d MMM HH:mm") +
                                    (File.Exists(b.BackupPath) ? "" : " - backup missing, will be skipped"),
                    Toggle = new ToggleButtonItem
                    {
                        IsChecked = selected,
                        Caption = "Restore",
                        CommandId = SettingsPageView.RestoreToggleCommandPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                };
            }));

        /// <summary>The listed videos that are selected, in list order.</summary>
        internal static List<BackupEntry> SelectedBackups(IList<BackupEntry> listed, IEnumerable<int> selection) =>
            (selection ?? Enumerable.Empty<int>())
                .Where(i => i >= 0 && i < Math.Min(listed.Count, RestoreListLimit))
                .Distinct()
                .OrderBy(i => i)
                .Select(i => listed[i])
                .ToList();

        internal static string RestoreRefusal(bool busy, int backups)
        {
            if (busy) return "Nothing restored: SubRemover is running. Try again when it has finished.";
            if (backups == 0) return "There are no backups to restore.";
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

        private StatusItem BackupCount()
        {
            try
            {
                var count = _state.Load().Backups.Count;
                return new StatusItem(RestoreCaption, count == 1 ? "1 video has a backup." : $"{count} videos have a backup.", ItemStatus.None);
            }
            catch (Exception ex)
            {
                return new StatusItem(RestoreCaption, "Backups unavailable: " + ex.Message, ItemStatus.Unknown);
            }
        }

        internal static void ApplyQuickSetup(PluginOptions options)
        {
            options.KeepLanguages = "eng";
            options.KeepUndefined = true;
        }

        internal PluginOptions PrepareForDisplay(PluginOptions options)
        {
            options.LibraryChoices = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType != "music" && f.CollectionType != "books" && f.CollectionType != "photos")
                .Select(f => new EditorSelectOption { Value = f.ItemId, Name = f.Name, IsEnabled = true })
                .ToList();
            options.LanguageChoices = LanguageChoicesFor(options.KeepLanguages);
            options.DefaultLanguageChoices = DefaultLanguageChoicesFor(options.KeepLanguages);
            if (options.DefaultLanguageChoices.All(c => c.Value != options.DefaultLanguage)) options.DefaultLanguage = string.Empty;
            options.RunStatus = CurrentStatus();
            options.RestoreStatus = BackupCount();
            options.RestoreItems = new GenericItemList();
            options.RestoreListShown = null;
            options.RestoreButton = PluginOptions.NewRestoreButton();
            options.SelectedLog = null;
            ShowLog(options);
            return options;
        }

        /// <summary>Fills the log list and shows the selected run log, or the newest when none is selected.</summary>
        internal void ShowLog(PluginOptions options)
        {
            try
            {
                var files = RunLog.ListFiles(_logDirectory);
                options.LogChoices = files.Select(f => new EditorSelectOption { Value = f, Name = f, IsEnabled = true }).ToList();
                if (!files.Contains(options.SelectedLog)) options.SelectedLog = files.FirstOrDefault();
                options.LogText = RunLog.ReadForDisplay(_logDirectory, options.SelectedLog);
            }
            catch (Exception ex)
            {
                options.LogText = "Couldn't read the run log: " + ex.Message;
            }
        }

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
