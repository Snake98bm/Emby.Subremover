using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Events;
using Emby.Web.GenericEdit.Elements;
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

    /// <summary>
    /// Remembers the restore list shown on the page and which of its videos are selected, until it's confirmed or replaced.
    /// Kept by the plugin rather than the page, which Emby may build again between clicks; a list left alone for more than
    /// a few minutes no longer counts.
    /// </summary>
    internal sealed class RestoreConfirmation
    {
        public static readonly TimeSpan ValidFor = TimeSpan.FromMinutes(10);

        private readonly HashSet<int> _selected = new HashSet<int>();
        private string _shown;
        private DateTime _shownUtc;

        /// <summary>A new list, with nothing selected.</summary>
        public void Show(string key, DateTime nowUtc)
        {
            _shown = key;
            _shownUtc = nowUtc;
            _selected.Clear();
        }

        public bool IsSelected(int index) => _selected.Contains(index);

        public IReadOnlyCollection<int> Selected => _selected.ToList();

        /// <summary>Selects or unselects one video; using the list keeps it current.</summary>
        public void Toggle(int index, DateTime nowUtc)
        {
            if (!_selected.Remove(index)) _selected.Add(index);
            _shownUtc = nowUtc;
        }

        /// <summary>Selects all of them, or unselects all when they're all selected already.</summary>
        public void ToggleAll(int count, DateTime nowUtc)
        {
            var all = _selected.Count == count;
            _selected.Clear();
            if (!all) for (var i = 0; i < count; i++) _selected.Add(i);
            _shownUtc = nowUtc;
        }

        /// <returns>The list shown by the previous click, and forgets it.</returns>
        public string Take(DateTime nowUtc)
        {
            var shown = nowUtc - _shownUtc <= ValidFor ? _shown : null;
            _shown = null;
            return shown;
        }
    }

    internal sealed class SettingsPageView : IPluginPageView
    {
        public const string QuickSetupCommand = "SubremoverQuickSetup";
        public const string RunNowCommand = "SubremoverRunNow";
        public const string SaveCommand = "SubremoverSave";
        public const string ShowLogCommand = "SubremoverShowLog";
        public const string RestoreCommand = "SubremoverRestore";
        public const string RestoreConfirmCommand = "SubremoverRestoreConfirm";
        public const string RestoreSelectAllCommand = "SubremoverRestoreSelectAll";

        // One video's toggle on the restore list; its number on the list follows the prefix.
        public const string RestoreToggleCommandPrefix = "SubremoverRestoreToggle:";

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
        // Hidden: the page has its own Save button next to Run now.
        public bool ShowSave { get; set; } = false;
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

                case SaveCommand:
                    Save();
                    return Task.FromResult<IPluginUIView>(this);

                case RunNowCommand:
                    Save();
                    Options.RunStatus = _plugin.RunNow();
                    UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                    return Task.FromResult<IPluginUIView>(this);

                case RestoreCommand:
                    ListRestore();
                    UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                    return Task.FromResult<IPluginUIView>(this);

                case RestoreConfirmCommand:
                    ConfirmRestore();
                    UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                    return Task.FromResult<IPluginUIView>(this);

                case RestoreSelectAllCommand:
                    ToggleSelectAll();
                    UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                    return Task.FromResult<IPluginUIView>(this);

                case ShowLogCommand:
                    _plugin.ShowLog(Options);
                    return Task.FromResult<IPluginUIView>(this);

                default:
                    if (commandId != null && commandId.StartsWith(RestoreToggleCommandPrefix, StringComparison.Ordinal)
                        && int.TryParse(commandId.Substring(RestoreToggleCommandPrefix.Length), out var index))
                    {
                        ToggleOne(index);
                        UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
                        return Task.FromResult<IPluginUIView>(this);
                    }
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

        // Two separate commands, so a repeated or stray command can never restore: "Restore originals from backups…" only
        // lists every video with a backup (none selected) with a warning; "Yes, restore…" restores the selected ones, and only
        // right after that same list was shown. Any other click or a changed set of backups needs a new list.
        private void ListRestore()
        {
            Save();
            var (refusal, backups) = _plugin.RestorePlan();
            if (refusal != null)
            {
                Options.RestoreStatus = new StatusItem(Plugin.RestoreCaption, refusal, ItemStatus.Warning);
                return;
            }

            _plugin.RestoreConfirmation.Show(RestoreKey(backups), DateTime.UtcNow);
            ShowRestoreList(backups);
            var more = backups.Count > Plugin.RestoreListLimit
                ? $" Only the newest {Plugin.RestoreListLimit} of {backups.Count} are listed; restore older ones in another go."
                : "";
            Options.RestoreStatus = new StatusItem(Plugin.RestoreCaption,
                "Select the videos to restore below, then click 'Yes, restore…'. Warning: each selected video is replaced by the original " +
                "from its backup, undoing everything SubRemover changed in it. Subtitle files SubRemover saved next to them stay." + more,
                ItemStatus.Warning);
        }

        // Selecting only changes the list. Nothing is saved, so the shown list stays the one to confirm.
        private void ToggleSelectAll()
        {
            var (_, backups) = _plugin.RestorePlan();
            _plugin.RestoreConfirmation.ToggleAll(Math.Min(backups.Count, Plugin.RestoreListLimit), DateTime.UtcNow);
            ShowRestoreList(backups);
        }

        private void ToggleOne(int index)
        {
            var (_, backups) = _plugin.RestorePlan();
            if (index >= 0 && index < Math.Min(backups.Count, Plugin.RestoreListLimit)) _plugin.RestoreConfirmation.Toggle(index, DateTime.UtcNow);
            ShowRestoreList(backups);
        }

        private void ShowRestoreList(IList<BackupEntry> backups)
        {
            var listed = Math.Min(backups.Count, Plugin.RestoreListLimit);
            var selection = _plugin.RestoreConfirmation;
            Options.RestoreItems = Plugin.RestoreItemsFor(backups, selection.IsSelected);
            Options.RestoreListShown = "yes";
            Options.RestoreSelectAllButton = PluginOptions.NewSelectAllButton(listed > 0 && selection.Selected.Count == listed);
            Options.RestoreButton = PluginOptions.NewRestoreButton(backups.Count);
        }

        private void ConfirmRestore()
        {
            var selection = _plugin.RestoreConfirmation.Selected;
            var shown = _plugin.RestoreConfirmation.Take(DateTime.UtcNow);
            Save();
            var (refusal, backups) = _plugin.RestorePlan();
            if (refusal != null)
            {
                Options.RestoreStatus = new StatusItem(Plugin.RestoreCaption, refusal, ItemStatus.Warning);
                return;
            }
            if (shown == null || shown != RestoreKey(backups))
            {
                Options.RestoreStatus = new StatusItem(Plugin.RestoreCaption,
                    "Nothing restored: the list changed or is no longer current. Click 'Restore originals from backups…' to see it again.", ItemStatus.Warning);
                return;
            }

            var chosen = Plugin.SelectedBackups(backups, selection);
            if (chosen.Count == 0)
            {
                Options.RestoreStatus = new StatusItem(Plugin.RestoreCaption, "Nothing restored: no video was selected.", ItemStatus.Warning);
                return;
            }
            Options.RestoreStatus = _plugin.StartRestore(chosen);
        }

        private static string RestoreKey(IList<BackupEntry> backups) => string.Join("|", backups.Select(b => b.OriginalPath));

        private void Save()
        {
            _plugin.RestoreConfirmation.Take(DateTime.UtcNow);
            Options.ValidateOrThrow();
            _plugin.Save(Options);
            Options = _plugin.PrepareForDisplay(Options);
            UIViewInfoChanged?.Invoke(this, new GenericEventArgs<IPluginUIView>(this));
        }
    }
}
