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
    public void Only_the_selected_videos_are_restored()
    {
        var backups = Enumerable.Range(0, 4).Select(i => new BackupEntry { OriginalPath = $"v{i}.mkv", BackupPath = $"v{i}.bak" }).ToList();

        Assert.Equal(new[] { "v0.mkv", "v3.mkv" }, Plugin.SelectedBackups(backups, new[] { 3, 0, 0, 9, -1 }).Select(b => b.OriginalPath));
        Assert.Empty(Plugin.SelectedBackups(backups, Array.Empty<int>()));
        Assert.Empty(Plugin.SelectedBackups(backups, null));
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

        // The libraries by name, keeping the id; one Emby no longer has stays as its id.
        options.Libraries = "12082,12083, 99";
        var named = options.DescribeSettings(new Dictionary<string, string> { ["12082"] = "Movies", ["12083"] = "TV Shows" });
        Assert.Contains("Libraries to process: Movies (12082), TV Shows (12083), 99", named);
        Assert.Contains("Libraries to process: 12082,12083, 99", options.DescribeSettings());
        options.Libraries = "";
        Assert.Contains("Libraries to process: (empty)", options.DescribeSettings(new Dictionary<string, string>()));
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
    public void Log_link_secret_lasts_a_day_and_only_matches_itself()
    {
        var secret = new LogLinkKey();
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        var key = secret.Current(now);
        Assert.Equal(32, key.Length);
        Assert.Equal(key, secret.Current(now.AddHours(23)));
        Assert.True(secret.IsValid(key, now.AddHours(1)));
        Assert.False(secret.IsValid(key.Substring(1) + "0", now));
        Assert.False(secret.IsValid("", now));
        Assert.False(secret.IsValid(null, now));
        Assert.False(secret.IsValid(key, now + LogLinkKey.ValidFor + TimeSpan.FromMinutes(1)));

        var next = secret.Current(now + LogLinkKey.ValidFor + TimeSpan.FromMinutes(1));
        Assert.NotEqual(key, next);
        Assert.False(new LogLinkKey().IsValid(key, now));
    }

    [Fact]
    public void Whole_log_is_read_even_while_a_run_is_writing_it()
    {
        var path = Path.Combine(_dir, "subremover-20261029-120000.txt");
        File.WriteAllText(path, "line 1\nline 2");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Equal("line 1\nline 2", RunLog.ReadAll(_dir, "subremover-20261029-120000.txt"));
        }
    }

    // ---- the settings page (Configuration/settings.html, drawn from SettingsLayout by settings.js)

    private static bool IsSetting(PropertyInfo p) =>
        (p.PropertyType == typeof(bool) || p.PropertyType == typeof(string) || p.PropertyType == typeof(int) || p.PropertyType.IsEnum)
        && p.CanWrite
        && p.GetCustomAttribute<System.ComponentModel.BrowsableAttribute>()?.Browsable != false
        && !string.IsNullOrEmpty(p.GetCustomAttribute<System.ComponentModel.DisplayNameAttribute>()?.DisplayName);

    [Fact]
    public void Every_setting_is_on_the_page_exactly_once()
    {
        var settings = typeof(PluginOptions).GetProperties().Where(IsSetting).Select(p => p.Name).OrderBy(n => n).ToList();
        var shown = SettingsLayout.FieldNames.ToList();

        Assert.NotEmpty(settings);
        Assert.Equal(shown.Count, shown.Distinct().Count());
        Assert.Equal(settings, shown.OrderBy(n => n));
    }

    [Fact]
    public void Page_has_its_tabs_in_order_with_the_run_restore_and_log_parts_where_planned()
    {
        var tabs = SettingsLayout.Page();

        Assert.Equal(new[] { "Run", "Subtitles", "Subtitle files", "Backups", "Logs" }, tabs.Select(t => t.Name));
        Assert.Equal(SettingsLayout.RunWidget, tabs[0].Entries[0].Text);
        Assert.Equal(nameof(PluginOptions.DryRun), tabs[0].Entries[1].Field.Name);
        Assert.Contains(tabs[1].Entries, e => e.Kind == SettingsLayout.Widget && e.Text == SettingsLayout.QuickSetupWidget);
        Assert.Contains(tabs[3].Entries, e => e.Kind == SettingsLayout.Widget && e.Text == SettingsLayout.RestoreWidget);
        Assert.Contains(tabs[4].Entries, e => e.Kind == SettingsLayout.Widget && e.Text == SettingsLayout.LogsWidget);
    }

    [Fact]
    public void Each_tab_ends_with_the_help_for_its_own_settings_in_order()
    {
        foreach (var tab in SettingsLayout.Page())
        {
            Assert.NotEmpty(tab.Help);
            Assert.Equal(SettingsHelp.For(Enum.Parse<SettingsTabId>(tab.Id)).Select(e => e.Setting), tab.Help.Select(e => e.Setting));
            foreach (var field in tab.Entries.Where(e => e.Kind == SettingsLayout.Field))
                Assert.Contains(tab.Help, e => field.Field.Label.StartsWith(e.Setting));
        }
        Assert.StartsWith("When on, nothing is changed", SettingsHelp.For(SettingsTabId.Run)[1].Text);
    }

    [Fact]
    public void Page_draws_each_setting_from_its_property()
    {
        var dryRun = SettingsLayout.Describe(nameof(PluginOptions.DryRun));
        Assert.Equal(("bool", "Test mode (dry run)", "Nothing is changed; the log shows what would happen."), (dryRun.Kind, dryRun.Label, dryRun.Description));

        Assert.Equal(("multiselect", "Libraries"), (SettingsLayout.Describe(nameof(PluginOptions.Libraries)).Kind, SettingsLayout.Describe(nameof(PluginOptions.Libraries)).Source));
        Assert.Equal(("multiselect", "Languages"), (SettingsLayout.Describe(nameof(PluginOptions.KeepLanguages)).Kind, SettingsLayout.Describe(nameof(PluginOptions.KeepLanguages)).Source));
        Assert.Equal(("select", "DefaultLanguage"), (SettingsLayout.Describe(nameof(PluginOptions.DefaultLanguage)).Kind, SettingsLayout.Describe(nameof(PluginOptions.DefaultLanguage)).Source));

        var max = SettingsLayout.Describe(nameof(PluginOptions.MaxItemsPerRun));
        Assert.Equal(("int", 0, 100000), (max.Kind, max.Min!.Value, max.Max!.Value));

        var mode = SettingsLayout.Describe(nameof(PluginOptions.BackupMode));
        Assert.Equal("select", mode.Kind);
        Assert.Contains(mode.Options!, o => o.Value == "NextToOriginal" && o.Name == "Next to original");

        var folder = SettingsLayout.Describe(nameof(PluginOptions.BackupFolder));
        Assert.Equal("folder", folder.Kind);
        Assert.Equal(("BackupMode", "IsEqual", "BackupFolder"), (folder.VisibleIf!.Field, folder.VisibleIf.Is, folder.VisibleIf.Value));

        var guessOnly = SettingsLayout.Describe(nameof(PluginOptions.GuessForFilesOnly));
        Assert.Equal(("GuessLanguages", "IsTrue"), (guessOnly.VisibleIf!.Field, guessOnly.VisibleIf.Is));
        Assert.Null(dryRun.VisibleIf);
    }

    [Fact]
    public void Saving_reads_each_value_from_its_text_and_ignores_names_the_page_does_not_show()
    {
        var options = new PluginOptions();
        var problem = SettingsLayout.Apply(options, new Dictionary<string, string>
        {
            [nameof(PluginOptions.DryRun)] = "false",
            [nameof(PluginOptions.MaxItemsPerRun)] = " 3 ",
            [nameof(PluginOptions.BackupMode)] = "None",
            [nameof(PluginOptions.KeepLanguages)] = "eng,spa",
            [nameof(PluginOptions.DefaultLanguage)] = "spa",
            [nameof(PluginOptions.OnlyPathContains)] = "  My Movie (2020)  ",
            ["Bogus"] = "x"
        });

        Assert.Null(problem);
        Assert.False(options.DryRun);
        Assert.Equal(3, options.MaxItemsPerRun);
        Assert.Equal(BackupMode.None, options.BackupMode);
        Assert.Equal("spa", options.DefaultLanguage);
        Assert.Equal("My Movie (2020)", options.OnlyPathContains);

        // What the page gets back is what it sends.
        var copy = new PluginOptions();
        Assert.Null(SettingsLayout.Apply(copy, SettingsLayout.Values(options)));
        Assert.Equal(options.DescribeSettings(), copy.DescribeSettings());
        Assert.Equal("false", SettingsLayout.Values(options)[nameof(PluginOptions.DryRun)]);
    }

    [Theory]
    [InlineData(nameof(PluginOptions.MaxItemsPerRun), "-1", "'Max videos to change per run' must be between 0 and 100000.")]
    [InlineData(nameof(PluginOptions.MaxItemsPerRun), "lots", "'Max videos to change per run' must be a whole number.")]
    [InlineData(nameof(PluginOptions.DryRun), "maybe", "'Test mode (dry run)' must be on or off.")]
    [InlineData(nameof(PluginOptions.BackupMode), "Sideways", "'Backup of the original file' has no choice 'Sideways'.")]
    public void A_bad_value_is_refused_by_the_setting_name(string name, string value, string expected)
    {
        var options = new PluginOptions();
        Assert.Equal(expected, SettingsLayout.Apply(options, new Dictionary<string, string> { [name] = value }));
        Assert.Equal(new PluginOptions().DescribeSettings(), options.DescribeSettings());
    }

    [Fact]
    public void A_default_language_that_is_no_longer_kept_goes_back_to_automatic()
    {
        var options = new PluginOptions { KeepLanguages = "eng,spa", DefaultLanguage = "spa" };
        SettingsLayout.Apply(options, new Dictionary<string, string> { [nameof(PluginOptions.KeepLanguages)] = "eng" });
        Assert.Equal("", options.DefaultLanguage);
    }

    [Fact]
    public void Settings_page_and_its_script_are_in_the_plugin()
    {
        var resources = typeof(Plugin).Assembly.GetManifestResourceNames();
        Assert.Contains("Emby.Subremover.Configuration.settings.html", resources);
        Assert.Contains("Emby.Subremover.Configuration.settings.js", resources);

        // The page names its script by the script's page name.
        using var html = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream("Emby.Subremover.Configuration.settings.html")!);
        Assert.Contains("data-controller=\"__plugin/" + Plugin.PageName + "Js\"", html.ReadToEnd());
        Assert.True(new PluginOptions().ShowInSidebar);
    }

    [Fact]
    public void Only_the_listed_backups_are_restored_and_only_from_the_list_that_was_shown()
    {
        var backups = Enumerable.Range(0, Plugin.RestoreListLimit + 5).Select(i => new BackupEntry { OriginalPath = $"v{i}.mkv", BackupPath = $"v{i}.bak" }).ToList();
        Assert.Equal(Plugin.RestoreListLimit, Plugin.SelectedBackups(backups, Enumerable.Range(0, Plugin.RestoreListLimit + 5)).Count);

        Assert.Equal(new[] { 0, 3, 5 }, SettingsApi.ParseIndexes("0, 3,x,-2,5"));
        Assert.Empty(SettingsApi.ParseIndexes(null));

        var key = SettingsApi.RestoreKey(backups);
        Assert.Equal(key, SettingsApi.RestoreKey(backups.ToList()));
        Assert.NotEqual(key, SettingsApi.RestoreKey(backups.Skip(1)));
    }

    [Fact]
    public void Log_links_open_or_download_the_picked_log()
    {
        Assert.Equal("/emby/SubRemover/Log?Name=subremover-20261029-120000.txt&Key=abc123", GetSubremoverLog.Link("subremover-20261029-120000.txt", "abc123", false));
        Assert.Equal("/emby/SubRemover/Log?Name=subremover-20261029-120000.txt&Key=abc123&Download=true", GetSubremoverLog.Link("subremover-20261029-120000.txt", "abc123", true));
        Assert.Contains("Name=a%20b%26c.txt", GetSubremoverLog.Link("a b&c.txt", "k", false));
    }
}
