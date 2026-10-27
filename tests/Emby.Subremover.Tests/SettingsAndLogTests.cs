using System.Reflection;
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
        var settings = SettingsPageController.ForPluginList(null!, "SubRemover").PageInfo;
        var target = SettingsPageController.ForSidebar(null!, "SubRemover").PageInfo;
        Assert.True(settings.IsMainConfigPage);
        Assert.False(target.IsMainConfigPage);
        Assert.Equal("SubremoverMenu", target.Name);
        Assert.True(new PluginOptions().ShowInSidebar);

        var pages = Plugin.SidebarPages(showInSidebar: true).ToList();
        var html = pages[0];
        Assert.True(html.EnableInMainMenu);
        Assert.False(html.IsMainConfigPage);
        Assert.Null(html.MenuSection);
        Assert.Equal("subtitles_off", html.MenuIcon);
        Assert.Equal("SubRemover", html.DisplayName);
        Assert.False(Plugin.SidebarPages(showInSidebar: false).First().EnableInMainMenu);

        // The embedded files exist, the HTML loads the script page, and the script opens the SubRemover page.
        string Resource(string name)
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name);
            Assert.NotNull(stream);
            return new StreamReader(stream).ReadToEnd();
        }
        Assert.Contains("__plugin/" + pages[1].Name, Resource(html.EmbeddedResourcePath));
        var script = Resource(pages[1].EmbeddedResourcePath);
        Assert.Contains("genericui?PageId=", script);
        Assert.Contains(":" + SettingsPageController.SidebarPageName, script);
    }

    [Fact]
    public void Page_title_shows_the_name_and_version()
    {
        var version = typeof(Plugin).Assembly.GetName().Version!;
        Assert.Equal($"SubRemover v{version.Major}.{version.Minor}.{version.Build}", new PluginOptions().EditorTitle);
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
}
