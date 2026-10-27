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
        var show = true;
        var controller = new SettingsPageController(null!, "Subremover", () => show);

        var page = controller.PageInfo;
        Assert.True(page.EnableInMainMenu);
        Assert.True(page.IsMainConfigPage);
        Assert.Null(page.MenuSection);
        Assert.Equal("subtitles_off", page.MenuIcon);
        Assert.True(new PluginOptions().ShowInSidebar);

        show = false;
        Assert.False(controller.PageInfo.EnableInMainMenu);
        Assert.True(controller.PageInfo.IsMainConfigPage);
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
