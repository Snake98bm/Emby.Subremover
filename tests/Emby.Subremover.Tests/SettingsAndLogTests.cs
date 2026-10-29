using System.Reflection;
using Emby.Subremover.Core;
using Emby.Subremover.ScheduledTasks;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Subremover.Tests;

public sealed class SettingsAndLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subremover-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Quick_setup_keeps_only_english_and_untagged_subtitles()
    {
        var options = new PluginOptions { KeepLanguages = "spa,fre", KeepUndefined = false };

        Plugin.ApplyQuickSetup(options);

        Assert.Equal("eng", options.KeepLanguages);
        Assert.True(options.KeepUndefined);
    }

    [Fact]
    public void Buttons_send_the_commands_the_page_handles_and_sit_where_planned()
    {
        var options = new PluginOptions();
        Assert.Equal(SettingsPageView.RunNowCommand, options.RunNowButton.CommandId);
        Assert.Equal(SettingsPageView.QuickSetupCommand, options.QuickSetupButton.CommandId);

        var order = typeof(PluginOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name).ToList();
        Assert.Equal(nameof(PluginOptions.RunNowButton), order.First(n => n != nameof(PluginOptions.EditorTitle) && n != nameof(PluginOptions.EditorDescription)));
        Assert.Equal(order.IndexOf(nameof(PluginOptions.KeepLanguages)) - 1, order.IndexOf(nameof(PluginOptions.QuickSetupButton)));
    }

    [Fact]
    public void Settings_page_is_in_the_sidebar_and_the_plugin_list()
    {
        var show = true;
        var settings = SettingsPageController.ForPluginList(null!, "SubRemover").PageInfo;
        var sidebar = SettingsPageController.ForSidebar(null!, "SubRemover", () => show);

        Assert.True(settings.IsMainConfigPage);
        Assert.False(settings.EnableInMainMenu);

        // The sidebar entry is its own page (the one at genericui?PageId=82033f:SubremoverMenu), spelled SubRemover.
        var menu = sidebar.PageInfo;
        Assert.Equal("SubremoverMenu", menu.Name);
        Assert.Equal("SubRemover", menu.DisplayName);
        Assert.False(menu.IsMainConfigPage);
        Assert.True(menu.EnableInMainMenu);
        Assert.Null(menu.MenuSection);
        Assert.Equal("subtitles_off", menu.MenuIcon);
        Assert.True(new PluginOptions().ShowInSidebar);

        show = false;
        Assert.False(sidebar.PageInfo.EnableInMainMenu);
    }

    [Fact]
    public void Page_title_shows_the_name_and_version()
    {
        // Local builds carry an alpha number ("0.6.9-alpha.3"); releases don't. The build metadata after '+' is left out.
        var version = typeof(Plugin).Assembly.GetName().Version!;
        Assert.Matches($@"^SubRemover v{version.Major}\.{version.Minor}\.{version.Build}(-alpha\.\d+)?$", new PluginOptions().EditorTitle);
    }

    [Fact]
    public void Run_log_writes_every_level_to_its_own_file()
    {
        string path;
        using (var log = new RunLog(_dir, "subremover", new NullLogger()))
        {
            log.Info("checking {0} videos", 3);
            log.Debug("details for {0}", "a.mkv");
            log.Warn("odd {braces} kept");
            log.ErrorException("failed on {0}", new IOException("disk full"), "b.mkv");
            log.Summary("done: {0} changed", 2);
            path = log.FilePath;
        }

        Assert.Matches(@"subremover-\d{8}-\d{6}\.txt$", path);
        var text = File.ReadAllText(path);
        Assert.Contains("Info  checking 3 videos", text);
        Assert.Contains("Debug details for a.mkv", text);
        Assert.Contains("Warn  odd {braces} kept", text);
        Assert.Contains("Error failed on b.mkv", text);
        Assert.Contains("disk full", text);
        Assert.Contains("done: 2 changed", text);
    }

    [Fact]
    public void Log_viewer_lists_run_logs_newest_first_and_reads_them()
    {
        var older = Path.Combine(_dir, "subremover-20261027-090000.txt");
        var newer = Path.Combine(_dir, "subremover-restore-20261027-100000.txt");
        File.WriteAllText(older, "older run");
        File.WriteAllText(newer, "newer run");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(_dir, "embyserver.txt"), "not ours");

        Assert.Equal(new[] { Path.GetFileName(newer), Path.GetFileName(older) }, RunLog.ListFiles(_dir));
        Assert.Equal("older run", RunLog.ReadForDisplay(_dir, Path.GetFileName(older)));
    }

    [Theory]
    [InlineData("embyserver.txt")]
    [InlineData("../secret.txt")]
    [InlineData(@"..\subremover-x.txt")]
    [InlineData(null)]
    public void Log_viewer_only_reads_run_logs(string requested)
    {
        var logs = Directory.CreateDirectory(Path.Combine(_dir, "logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "embyserver.txt"), "server log");
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(_dir, "subremover-x.txt"), "outside the log folder");

        var text = RunLog.ReadForDisplay(logs, requested);

        Assert.StartsWith("No run log yet", text);
    }

    [Fact]
    public void Long_logs_show_the_end()
    {
        var name = "subremover-20261027-110000.txt";
        File.WriteAllText(Path.Combine(_dir, name), new string('a', 500) + "LAST LINE");

        var text = RunLog.ReadForDisplay(_dir, name, maxChars: 100);

        Assert.StartsWith("… (start of the log cut off", text);
        Assert.EndsWith("LAST LINE", text);
    }

    [Fact]
    public void Display_only_fields_are_cleared_before_saving()
    {
        var options = new PluginOptions
        {
            LogText = "big log",
            SelectedLog = "subremover-1.txt",
            LogChoices = { new Emby.Web.GenericEdit.Common.EditorSelectOption { Value = "x" } },
            LanguageChoices = { new Emby.Web.GenericEdit.Common.EditorSelectOption { Value = "eng" } },
            KeepLanguages = "eng,spa"
        };

        options.ClearDisplayOnlyFields();

        Assert.Null(options.LogText);
        Assert.Null(options.SelectedLog);
        Assert.Empty(options.LogChoices);
        Assert.Empty(options.LanguageChoices);
        Assert.Equal("eng,spa", options.KeepLanguages);
    }

    [Fact]
    public void Only_the_newest_run_logs_are_kept()
    {
        for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(_dir, $"subremover-20260101-0000{i:00}.txt"), "old");
        File.WriteAllText(Path.Combine(_dir, "embyserver.txt"), "not ours");

        using (new RunLog(_dir, "subremover", new NullLogger())) { }

        var ours = Directory.GetFiles(_dir, "subremover-*.txt");
        Assert.Equal(RunLog.KeepFiles, ours.Length);
        Assert.DoesNotContain(ours, f => f.EndsWith("subremover-20260101-000000.txt"));
        Assert.True(File.Exists(Path.Combine(_dir, "embyserver.txt")));
    }

    [Fact]
    public void Default_language_choices_are_automatic_plus_the_kept_languages()
    {
        var choices = Plugin.DefaultLanguageChoicesFor("spa,eng,und");

        Assert.Equal(new[] { "", "eng", "spa" }, choices.Select(c => c.Value));
        Assert.Equal("English (eng)", choices[1].Name);
    }

    [Theory]
    [InlineData(true, 1, "SubRemover is running")]
    [InlineData(false, 0, "There are no backups to restore.")]
    [InlineData(false, 5, null)]
    public void Restore_button_only_refuses_while_running_or_without_backups(bool busy, int count, string expected)
    {
        var refusal = Plugin.RestoreRefusal(busy, count);

        if (expected == null) Assert.Null(refusal);
        else Assert.Contains(expected, refusal);
    }

    [Fact]
    public void Restore_list_shows_the_newest_backups_first()
    {
        var backups = new[]
        {
            new BackupEntry { OriginalPath = "old.mkv", CreatedUtc = new DateTime(2026, 10, 1) },
            new BackupEntry { OriginalPath = "new.mkv", CreatedUtc = new DateTime(2026, 10, 29) },
            new BackupEntry { OriginalPath = "mid.mkv", CreatedUtc = new DateTime(2026, 10, 15) },
        };

        Assert.Equal(new[] { "new.mkv", "mid.mkv", "old.mkv" }, Plugin.NewestFirst(backups).Select(b => b.OriginalPath));
    }

    [Fact]
    public void Restore_matches_backups_by_path_text()
    {
        var backups = new[]
        {
            new BackupEntry { OriginalPath = @"\\nas\Movies\My Movie (2020)\My Movie (2020).mkv" },
            new BackupEntry { OriginalPath = @"\\nas\TV\My Show\S01E01.mkv" },
        };

        Assert.Single(RestoreBackupsTask.Matching(backups, "my movie"));
        Assert.Equal(2, RestoreBackupsTask.Matching(backups, "").Count);
    }

    [Fact]
    public void Restore_list_shows_each_video_and_missing_backups()
    {
        var backup = Path.Combine(_dir, "My Movie (2020).mkv.subremover.bak");
        File.WriteAllText(backup, "original");
        var backups = new List<BackupEntry>
        {
            new() { OriginalPath = Path.Combine(_dir, "My Movie (2020).mkv"), BackupPath = backup },
            new() { OriginalPath = Path.Combine(_dir, "Other (2021).mkv"), BackupPath = Path.Combine(_dir, "gone.bak") },
        };

        var items = Plugin.RestoreItemsFor(backups, i => i == 1);

        Assert.Equal("My Movie (2020).mkv", items[0].PrimaryText);
        Assert.StartsWith(_dir, items[0].SecondaryText);
        Assert.EndsWith("backup missing, will be skipped", items[1].SecondaryText);
        Assert.False(items[0].Toggle.IsChecked);
        Assert.True(items[1].Toggle.IsChecked);
        Assert.All(items, item => Assert.Equal("Restore", item.Toggle.Caption));
        Assert.Equal(SettingsPageView.RestoreToggleCommandPrefix + "1", items[1].Toggle.CommandId);
    }

    [Fact]
    public void Restore_list_is_cut_short_for_big_restores()
    {
        var backups = Enumerable.Range(0, Plugin.RestoreListLimit + 5).Select(i => new BackupEntry { OriginalPath = $"v{i}.mkv", BackupPath = $"v{i}.bak" }).ToList();

        Assert.Equal(Plugin.RestoreListLimit, Plugin.RestoreItemsFor(backups, _ => false).Count);
        Assert.Equal(Plugin.RestoreListLimit, Plugin.SelectedBackups(backups, Enumerable.Range(0, Plugin.RestoreListLimit + 5)).Count);
    }

    [Fact]
    public void Only_the_selected_videos_are_restored()
    {
        var backups = Enumerable.Range(0, 4).Select(i => new BackupEntry { OriginalPath = $"v{i}.mkv", BackupPath = $"v{i}.bak" }).ToList();

        Assert.Equal(new[] { "v0.mkv", "v3.mkv" }, Plugin.SelectedBackups(backups, new[] { 3, 0, 0, 9, -1 }).Select(b => b.OriginalPath));
        Assert.Empty(Plugin.SelectedBackups(backups, Array.Empty<int>()));
        Assert.Empty(Plugin.SelectedBackups(backups, null));
    }

    [Fact]
    public void The_restore_list_remembers_what_is_selected_and_starts_empty()
    {
        var list = new RestoreConfirmation();
        var now = DateTime.UtcNow;
        list.Show("a|b|c", now);
        Assert.Empty(list.Selected);

        list.Toggle(1, now);
        Assert.True(list.IsSelected(1));
        list.Toggle(1, now);
        Assert.False(list.IsSelected(1));

        list.ToggleAll(3, now);
        Assert.Equal(new[] { 0, 1, 2 }, list.Selected.OrderBy(i => i));
        list.ToggleAll(3, now);
        Assert.Empty(list.Selected);

        list.Toggle(2, now);
        list.Show("a|b|c", now);
        Assert.Empty(list.Selected);
    }

    [Fact]
    public void A_restore_list_only_confirms_the_very_next_click()
    {
        var confirmation = new RestoreConfirmation();
        var now = DateTime.UtcNow;
        Assert.Null(confirmation.Take(now));

        confirmation.Show("My Movie (2020)|a.mkv", now);
        Assert.Equal("My Movie (2020)|a.mkv", confirmation.Take(now));
        Assert.Null(confirmation.Take(now));
    }

    [Fact]
    public void Restore_button_asks_for_confirmation_after_the_list_is_shown()
    {
        Assert.Equal("Restore originals from backups…", PluginOptions.NewRestoreButton().Caption);
        Assert.Equal("Yes, restore this video", PluginOptions.NewRestoreButton(1).Caption);
        Assert.Equal("Yes, restore the selected videos", PluginOptions.NewRestoreButton(3).Caption);

        // Listing and restoring are different commands, so a repeated "list" can never restore.
        Assert.Equal(SettingsPageView.RestoreCommand, PluginOptions.NewRestoreButton().CommandId);
        Assert.Equal(SettingsPageView.RestoreConfirmCommand, PluginOptions.NewRestoreButton(3).CommandId);
        Assert.NotEqual(SettingsPageView.RestoreCommand, SettingsPageView.RestoreConfirmCommand);

        // The list starts with nothing selected, so the one select button starts as "Select all".
        Assert.Equal("Select all", new PluginOptions().RestoreSelectAllButton.Caption);
        Assert.Equal("Unselect all", PluginOptions.NewSelectAllButton(allSelected: true).Caption);
        Assert.Equal(SettingsPageView.RestoreSelectAllCommand, new PluginOptions().RestoreSelectAllButton.CommandId);
    }

    [Fact]
    public void A_restore_list_expires_after_a_few_minutes()
    {
        var confirmation = new RestoreConfirmation();
        var shown = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        confirmation.Show("My Movie (2020)|a.mkv", shown);
        Assert.Null(confirmation.Take(shown + RestoreConfirmation.ValidFor + TimeSpan.FromSeconds(1)));

        confirmation.Show("My Movie (2020)|a.mkv", shown);
        Assert.Equal("My Movie (2020)|a.mkv", confirmation.Take(shown.AddMinutes(1)));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" ; ", true)]
    [InlineData("my movie (2020)", true)]
    [InlineData("Other Film; My Movie (2020)", true)]
    [InlineData("Other Film; Another One", false)]
    [InlineData("Rain, Wind And Coffee", false)]
    public void Path_filter_takes_several_texts_separated_by_semicolons(string filter, bool matches)
    {
        Assert.Equal(matches, PathFilter.Matches(@"\\nas\Movies\My Movie (2020)\My Movie (2020).mkv", filter));
    }

    [Fact]
    public void A_title_with_a_comma_still_matches_as_one_text()
    {
        Assert.True(PathFilter.Matches(@"\\nas\TV\My Show\My Show - S01E08 - Rain, Wind And Coffee.mkv", "Rain, Wind And Coffee"));
        Assert.True(PathFilter.IsEmpty(" ; "));
    }

    [Fact]
    public void Run_log_lists_every_setting_but_not_page_only_fields()
    {
        var options = new PluginOptions { KeepLanguages = "eng,spa", OnlyPathContains = "My Movie (2020); My Show", GuessLanguages = true };

        var lines = options.DescribeSettings();

        Assert.Contains("Test mode (dry run): on", lines);
        Assert.Contains("Subtitle languages to keep: eng,spa", lines);
        Assert.Contains("Guess the language of subtitles with no language set: on", lines);
        Assert.Contains("Only process paths containing: My Movie (2020); My Show", lines);
        Assert.Contains("Default subtitle language: (empty)", lines);
        Assert.Contains(lines, l => l.StartsWith("Backup of the original file: NextToOriginal"));
        Assert.DoesNotContain(lines, l => l.StartsWith("Run log") || l.Contains("Choices") || l.StartsWith("Videos that will be restored"));
    }

    [Fact]
    public void Every_setting_has_a_help_entry()
    {
        var settings = typeof(PluginOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) || p.PropertyType == typeof(string) || p.PropertyType == typeof(int) || p.PropertyType.IsEnum)
            .Select(p => p.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();

        Assert.NotEmpty(settings);
        Assert.All(settings, name => Assert.Contains(SettingsHelp.Entries, e => name!.StartsWith(e.Setting)));
        Assert.All(SettingsHelp.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Text)));
    }

    [Fact]
    public void Help_shows_every_entry_in_order_with_its_title_and_explanation()
    {
        var shown = typeof(PluginOptions).GetProperties()
            .Where(p => p.Name.StartsWith("Help") && p.PropertyType == typeof(StatusItem))
            .Select(p => (StatusItem)p.GetValue(new PluginOptions())!)
            .ToList();

        Assert.Equal(SettingsHelp.Entries.Select(e => e.Setting), shown.Select(s => s.Caption));
        Assert.StartsWith("When on, nothing is changed", shown[1].StatusText);
    }
}
