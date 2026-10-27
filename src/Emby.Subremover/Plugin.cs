using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Subremover.Core;
using Emby.Subremover.ScheduledTasks;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
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
        private readonly List<IPluginUIPageController> _pages;

        public Plugin(IApplicationHost applicationHost, ILibraryManager libraryManager)
        {
            _applicationHost = applicationHost;
            _libraryManager = libraryManager;
            _store = new OptionsStore(
                applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath,
                Name,
                applicationHost.Resolve<IJsonSerializer>(),
                applicationHost.Resolve<ILogManager>().GetLogger(Name));
            _state = new StateStore(applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath, applicationHost.Resolve<IJsonSerializer>());
            _pages = new List<IPluginUIPageController> { new SettingsPageController(this, Name, () => Options.ShowInSidebar) };
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override string Name => "Subremover";

        public override string Description => "Removes unwanted embedded subtitle tracks from video files, with test mode and backups.";

        public override Guid Id => new Guid("82033f83-61d4-48e1-b20a-831041323b14");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream("Emby.Subremover.thumb.png");

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers => _pages.AsReadOnly();

        public PluginOptions Options => _store.Get();

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
            options.RunStatus = CurrentStatus();
            return options;
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
