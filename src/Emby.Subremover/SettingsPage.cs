using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;

namespace Emby.Subremover
{
    // Emby's automatic settings page can't react to buttons, so the plugin provides its own page with the same layout.
    internal sealed class SettingsPageController : IPluginUIPageController
    {
        private readonly Plugin _plugin;
        private readonly string _name;
        private readonly string _displayName;
        private readonly bool _isMainConfigPage;
        private readonly Func<bool> _showInSidebar;

        private SettingsPageController(Plugin plugin, string name, string displayName, bool isMainConfigPage, Func<bool> showInSidebar)
        {
            _plugin = plugin;
            _name = name;
            _displayName = displayName;
            _isMainConfigPage = isMainConfigPage;
            _showInSidebar = showInSidebar;
        }

        /// <summary>The page opened from Plugins → Subremover.</summary>
        public static SettingsPageController ForPluginList(Plugin plugin, string displayName) =>
            new SettingsPageController(plugin, "Settings", displayName, true, () => false);

        /// <summary>
        /// The same screen as a separate page for the server sidebar: Emby leaves a plugin's main settings page out of the
        /// sidebar, so the menu entry needs its own page.
        /// </summary>
        public static SettingsPageController ForSidebar(Plugin plugin, string displayName, Func<bool> showInSidebar) =>
            new SettingsPageController(plugin, SidebarPageName, displayName, false, showInSidebar);

        public const string SidebarPageName = "SubremoverMenu";

        // Built on every read so the sidebar setting can apply without reinstalling.
        public PluginPageInfo PageInfo => new PluginPageInfo
        {
            Name = _name,
            DisplayName = _displayName,
            IsMainConfigPage = _isMainConfigPage,
            // No MenuSection: Emby lists plugin pages under "Advanced" in the server sidebar.
            EnableInMainMenu = _showInSidebar(),
            MenuIcon = "subtitles_off"
        };

        public Task Initialize(CancellationToken token) => Task.CompletedTask;

        public Task<IPluginUIView> CreateDefaultPageView() =>
            Task.FromResult<IPluginUIView>(new SettingsPageView(_plugin, _plugin.PrepareForDisplay(_plugin.OptionsForEditing())));
    }

    internal sealed class SettingsPageView : IPluginPageView
    {
        public const string QuickSetupCommand = "SubremoverQuickSetup";
        public const string RunNowCommand = "SubremoverRunNow";
        public const string ShowLogCommand = "SubremoverShowLog";

        private readonly Plugin _plugin;

        public SettingsPageView(Plugin plugin, PluginOptions options)
        {
            _plugin = plugin;
            Options = options;
        }

        public PluginOptions Options { get; private set; }

        public string Caption => Options.EditorTitle;
        public string SubCaption => Options.EditorDescription;
        public string PluginId => _plugin.Name;

        public IEditableObject ContentData
        {
            get => Options;
            set => Options = (PluginOptions)value;
        }

        public UserDto User { get; set; }
        public string RedirectViewUrl { get; set; }
        public bool ShowSave { get; set; } = true;
        public bool ShowBack { get; set; }
        public bool AllowSave { get; set; } = true;
        public bool AllowBack { get; set; } = true;

        public event EventHandler<GenericEventArgs<IPluginUIView>> UIViewInfoChanged;

        public bool IsCommandAllowed(string commandKey) => true;

        public Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            switch (commandId)
            {
                case QuickSetupCommand:
                    Plugin.ApplyQuickSetup(Options);
                    Save();
                    return Task.FromResult<IPluginUIView>(this);

                case RunNowCommand:
                    Save();
                    Options.RunStatus = _plugin.RunNow();
                    UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                    return Task.FromResult<IPluginUIView>(this);

                case ShowLogCommand:
                    _plugin.ShowLog(Options);
                    return Task.FromResult<IPluginUIView>(this);

                default:
                    return Task.FromResult<IPluginUIView>(null);
            }
        }

        public Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            Save();
            return Task.FromResult<IPluginUIView>(this);
        }

        public Task Cancel() => Task.CompletedTask;

        public void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
        {
        }

        private void Save()
        {
            Options.ValidateOrThrow();
            _plugin.Save(Options);
            Options = _plugin.PrepareForDisplay(Options);
            UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        }
    }
}
