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
        private readonly string _displayName;
        private readonly Func<bool> _showInSidebar;

        public SettingsPageController(Plugin plugin, string displayName, Func<bool> showInSidebar)
        {
            _plugin = plugin;
            _displayName = displayName;
            _showInSidebar = showInSidebar;
        }

        // Built on every read so the sidebar setting applies without restarting Emby.
        public PluginPageInfo PageInfo => new PluginPageInfo
        {
            Name = "Settings",
            DisplayName = _displayName,
            IsMainConfigPage = true,
            // No MenuSection: Emby lists plugin pages under "Advanced" in the server sidebar.
            EnableInMainMenu = _showInSidebar(),
            MenuIcon = "subtitles_off"
        };

        public Task Initialize(CancellationToken token) => Task.CompletedTask;

        public Task<IPluginUIView> CreateDefaultPageView() =>
            Task.FromResult<IPluginUIView>(new SettingsPageView(_plugin, _plugin.PrepareForDisplay(_plugin.Options)));
    }

    internal sealed class SettingsPageView : IPluginPageView
    {
        public const string QuickSetupCommand = "SubremoverQuickSetup";
        public const string RunNowCommand = "SubremoverRunNow";

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
