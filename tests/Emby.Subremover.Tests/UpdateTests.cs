using System.Security.Cryptography;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit.Elements;
using Xunit;

namespace Emby.Subremover.Tests;

public class UpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sr-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // The DLL these tests run against: SubRemover itself, with the csproj's version.
    private static string BuiltDll => typeof(Plugin).Assembly.Location;
    private static PluginVersion BuiltVersion => PluginVersion.From(typeof(Plugin).Assembly.GetName().Version!);

    private static string Sha256(string file)
    {
        using var sha = SHA256.Create();
        using var s = File.OpenRead(file);
        return "sha256:" + Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
    }

    private sealed class FakeSource : IReleaseSource
    {
        public GitHubRelease Release;
        public string FileToServe = BuiltDll;
        public Exception Throw;
        public int Downloads;
        public TimeSpan Delay;

        public Task<GitHubRelease> Latest(CancellationToken cancellationToken) =>
            Throw != null ? Task.FromException<GitHubRelease>(Throw) : Task.FromResult(Release);

        public async Task Download(string url, string toFile, long maxBytes, CancellationToken cancellationToken)
        {
            Downloads++;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            File.Copy(FileToServe, toFile, true);
        }
    }

    private static GitHubRelease ReleaseOf(PluginVersion v, bool withAsset = true) => new()
    {
        tag_name = "v" + v,
        html_url = "https://github.com/Snake98bm/Emby.Subremover/releases/tag/v" + v,
        assets = withAsset
            ? new List<GitHubAsset> { new() { name = "Emby.Subremover.dll", size = new FileInfo(BuiltDll).Length, digest = Sha256(BuiltDll), browser_download_url = Updater.DownloadUrlStart + "v" + v + "/Emby.Subremover.dll" } }
            : new List<GitHubAsset>()
    };

    private (Updater Updater, UpdateStateStore Store, string Target) Make(FakeSource source, string running)
    {
        var plugins = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(plugins);
        var target = Path.Combine(plugins, "Emby.Subremover.dll");
        if (!File.Exists(target)) File.WriteAllText(target, "the old plugin");
        var store = new UpdateStateStore(Path.Combine(_dir, "config"), new TestJsonSerializer());
        return (new Updater(source, store, PluginVersion.Parse(running), target, Path.Combine(_dir, "config", "subremover-update")), store, target);
    }

    private static PluginVersion Older => PluginVersion.Parse(BuiltVersion.Parts[2] > 0
        ? $"{BuiltVersion.Parts[0]}.{BuiltVersion.Parts[1]}.{BuiltVersion.Parts[2] - 1}"
        : $"{BuiltVersion.Parts[0]}.{BuiltVersion.Parts[1] - 1}.99");

    // ---- versions

    [Theory]
    [InlineData("v1.0.7", "1.0.6", 1)]
    [InlineData("1.0.7", "1.0.7.0", 0)]
    [InlineData("1.0.10", "1.0.9", 1)]
    [InlineData("1.1", "1.0.99", 1)]
    [InlineData("1.0.7", "1.0.7-alpha.3", 1)]
    [InlineData("1.0.7-alpha.10", "1.0.7-alpha.9", 1)]
    [InlineData("1.0.6", "1.0.7-alpha.1", -1)]
    [InlineData("1.0.7+abc123", "1.0.7", 0)]
    public void Versions_compare_like_releases(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(PluginVersion.Parse(a).CompareTo(PluginVersion.Parse(b))));
        Assert.Equal(-expected, Math.Sign(PluginVersion.Parse(b).CompareTo(PluginVersion.Parse(a))));
    }

    [Theory]
    [InlineData("v1.0.7", "1.0.7")]
    [InlineData("1.0.7.0", "1.0.7")]
    [InlineData("1.0.7.2", "1.0.7.2")]
    [InlineData("1.0.7-alpha.3", "1.0.7-alpha.3")]
    public void Versions_print_without_the_v_and_empty_revision(string text, string printed) =>
        Assert.Equal(printed, PluginVersion.Parse(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.x")]
    public void Not_a_version(string text) => Assert.False(PluginVersion.TryParse(text, out _));

    // ---- GitHub's answer

    [Fact]
    public void GitHubs_latest_release_json_reads_into_the_release()
    {
        const string json = """
            {"url":"https://api.github.com/repos/Snake98bm/Emby.Subremover/releases/1","html_url":"https://github.com/Snake98bm/Emby.Subremover/releases/tag/v1.0.6",
             "tag_name":"v1.0.6","name":"v1.0.6","draft":false,"prerelease":false,"published_at":"2026-11-27T17:54:00Z","author":{"login":"github-actions[bot]"},
             "assets":[{"name":"Emby.Subremover.dll","size":812544,"digest":"sha256:00ff","browser_download_url":"https://github.com/Snake98bm/Emby.Subremover/releases/download/v1.0.6/Emby.Subremover.dll","uploader":{"login":"x"}}]}
            """;
        var r = new TestJsonSerializer().DeserializeFromString<GitHubRelease>(json);
        Assert.Equal("v1.0.6", r.tag_name);
        Assert.False(r.prerelease);
        var a = Assert.Single(r.assets);
        Assert.Equal(812544, a.size);
        Assert.Equal("sha256:00ff", a.digest);
        Assert.EndsWith("/v1.0.6/Emby.Subremover.dll", a.browser_download_url);
    }

    // ---- checking the download

    [Fact]
    public void The_real_plugin_dll_with_matching_size_digest_and_version_passes()
    {
        Assert.Null(UpdateInstaller.Verify(BuiltDll, BuiltVersion, new FileInfo(BuiltDll).Length, Sha256(BuiltDll)));
        Assert.Null(UpdateInstaller.Verify(BuiltDll, BuiltVersion, 0, null));
    }

    [Fact]
    public void A_download_that_isnt_the_release_fails_with_the_reason()
    {
        var size = new FileInfo(BuiltDll).Length;
        Assert.Contains("not " + Older, UpdateInstaller.Verify(BuiltDll, Older, size, Sha256(BuiltDll)));
        Assert.Contains("GitHub lists", UpdateInstaller.Verify(BuiltDll, BuiltVersion, size + 1, null));
        Assert.Contains("SHA-256", UpdateInstaller.Verify(BuiltDll, BuiltVersion, size, "sha256:" + new string('0', 64)));

        var junk = Path.Combine(_dir, "junk.dll");
        File.WriteAllText(junk, "<html>rate limited</html>");
        Assert.Contains("isn't a .NET DLL", UpdateInstaller.Verify(junk, BuiltVersion, 0, null));

        var other = typeof(Xunit.FactAttribute).Assembly.Location;
        Assert.Contains("not SubRemover", UpdateInstaller.Verify(other, BuiltVersion, 0, null));
    }

    // ---- putting it in place

    [Fact]
    public void Swap_moves_the_new_dll_in_and_keeps_a_copy_of_the_old_one()
    {
        var target = Path.Combine(_dir, "Emby.Subremover.dll");
        var fresh = Path.Combine(_dir, "download");
        var previous = Path.Combine(_dir, "previous", "Emby.Subremover.dll");
        File.WriteAllText(target, "old");
        File.WriteAllText(fresh, "new");
        File.WriteAllText(target + ".old", "left by an earlier update");

        UpdateInstaller.Swap(fresh, target, previous);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("old", File.ReadAllText(previous));
        Assert.False(File.Exists(target + ".new"));
        Assert.False(File.Exists(target + ".old")); // not locked on Linux, nor in this test on Windows
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Swap_refuses_when_the_plugin_file_is_missing()
    {
        var fresh = Path.Combine(_dir, "download");
        File.WriteAllText(fresh, "new");
        Assert.Throws<FileNotFoundException>(() => UpdateInstaller.Swap(fresh, Path.Combine(_dir, "nothing.dll"), Path.Combine(_dir, "p", "x.dll")));
    }

    // ---- the whole check

    [Fact]
    public async Task A_newer_release_is_downloaded_checked_and_put_in_place()
    {
        var source = new FakeSource { Release = ReleaseOf(BuiltVersion) };
        var (updater, store, target) = Make(source, Older.ToString());
        var log = new List<string>();

        Assert.Equal(UpdateOutcome.Installed, await updater.Run(install: true, manual: false, log.Add, CancellationToken.None));

        Assert.Equal(File.ReadAllBytes(BuiltDll), File.ReadAllBytes(target));
        Assert.Equal("the old plugin", File.ReadAllText(updater.PreviousCopy));
        var state = store.Load();
        Assert.Equal(BuiltVersion.ToString(), state.LatestVersion);
        Assert.Equal(BuiltVersion.ToString(), state.InstalledVersion);
        Assert.Null(state.Problem);
        Assert.Contains(log, l => l.Contains("starts working when Emby restarts"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!).Where(f => f != target));

        // The next check finds it already there and doesn't download it again.
        Assert.Equal(UpdateOutcome.WaitingForRestart, await updater.Run(true, false, null, CancellationToken.None));
        Assert.Equal(1, source.Downloads);
        var (text, status, available) = Updater.Describe(store.Load(), Older, installs: true);
        Assert.StartsWith($"v{BuiltVersion} is installed and starts working when Emby restarts", text);
        Assert.Equal(ItemStatus.Warning, status);
        Assert.Null(available);
    }

    [Fact]
    public async Task With_installing_off_a_newer_release_is_only_reported()
    {
        var source = new FakeSource { Release = ReleaseOf(BuiltVersion) };
        var (updater, store, target) = Make(source, Older.ToString());

        Assert.Equal(UpdateOutcome.Available, await updater.Run(install: false, manual: false, null, CancellationToken.None));

        Assert.Equal("the old plugin", File.ReadAllText(target));
        Assert.Equal(0, source.Downloads);
        var (text, _, available) = Updater.Describe(store.Load(), Older, installs: false);
        Assert.StartsWith($"v{BuiltVersion} is available; running v{Older}", text);
        Assert.Contains("Click Install now", text);
        Assert.Equal(BuiltVersion.ToString(), available);
    }

    [Fact]
    public async Task The_newest_release_running_is_up_to_date()
    {
        var (updater, store, target) = Make(new FakeSource { Release = ReleaseOf(BuiltVersion) }, BuiltVersion.ToString());

        Assert.Equal(UpdateOutcome.UpToDate, await updater.Run(true, false, null, CancellationToken.None));

        Assert.Equal("the old plugin", File.ReadAllText(target));
        var (text, status, _) = Updater.Describe(store.Load(), BuiltVersion, true);
        Assert.StartsWith($"Up to date: running v{BuiltVersion}", text);
        Assert.Equal(ItemStatus.Succeeded, status);
    }

    [Fact]
    public async Task A_local_build_newer_than_the_release_is_left_alone()
    {
        var newer = $"{BuiltVersion.Parts[0]}.{BuiltVersion.Parts[1]}.{BuiltVersion.Parts[2] + 1}-alpha.2";
        var (updater, _, target) = Make(new FakeSource { Release = ReleaseOf(BuiltVersion) }, newer);
        Assert.Equal(UpdateOutcome.UpToDate, await updater.Run(true, false, null, CancellationToken.None));
        Assert.Equal("the old plugin", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_bad_download_leaves_the_plugin_alone()
    {
        var junk = Path.Combine(_dir, "junk");
        File.WriteAllText(junk, "not a dll");
        var release = ReleaseOf(BuiltVersion);
        release.assets[0].size = 0;
        release.assets[0].digest = null;
        var (updater, store, target) = Make(new FakeSource { Release = release, FileToServe = junk }, Older.ToString());

        Assert.Equal(UpdateOutcome.Failed, await updater.Run(true, false, null, CancellationToken.None));

        Assert.Equal("the old plugin", File.ReadAllText(target));
        Assert.Contains("was not installed: it isn't a .NET DLL", store.Load().Problem);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "config", "subremover-update")));
    }

    [Fact]
    public async Task No_release_a_prerelease_a_missing_dll_or_no_connection_are_problems()
    {
        var pre = ReleaseOf(BuiltVersion);
        pre.prerelease = true;
        foreach (var (source, expected) in new (FakeSource, string)[]
                 {
                     (new FakeSource { Release = null }, "no SubRemover release"),
                     (new FakeSource { Release = pre }, "no SubRemover release"),
                     (new FakeSource { Release = ReleaseOf(BuiltVersion, withAsset: false) }, "has no Emby.Subremover.dll"),
                     (new FakeSource { Throw = new HttpRequestException("No such host is known") }, "The update check failed: No such host is known")
                 })
        {
            var (updater, store, target) = Make(source, Older.ToString());
            Assert.Equal(UpdateOutcome.Failed, await updater.Run(true, false, null, CancellationToken.None));
            Assert.Contains(expected, store.Load().Problem);
            Assert.Equal("the old plugin", File.ReadAllText(target));
            Assert.Equal(ItemStatus.Failed, Updater.Describe(store.Load(), Older, true).Status);
        }
    }

    [Fact]
    public async Task A_restart_that_didnt_take_isnt_repeated_but_install_now_still_works()
    {
        var (updater, store, target) = Make(new FakeSource { Release = ReleaseOf(BuiltVersion) }, Older.ToString());
        File.Copy(BuiltDll, target, true);
        var state = store.Load();
        state.RestartedFor = BuiltVersion.ToString();
        store.Save(state);

        Assert.Equal(UpdateOutcome.StillOld, await updater.Run(true, manual: false, null, CancellationToken.None));
        Assert.Contains("still running", store.Load().Problem);

        Assert.Equal(UpdateOutcome.WaitingForRestart, await updater.Run(true, manual: true, null, CancellationToken.None));
    }

    [Fact]
    public async Task After_the_restart_the_new_version_is_up_to_date_and_the_restart_is_forgotten()
    {
        var (updater, store, _) = Make(new FakeSource { Release = ReleaseOf(BuiltVersion) }, BuiltVersion.ToString());
        var state = store.Load();
        state.InstalledVersion = BuiltVersion.ToString();
        state.RestartedFor = BuiltVersion.ToString();
        store.Save(state);

        Assert.Equal(UpdateOutcome.UpToDate, await updater.Run(true, false, null, CancellationToken.None));
        Assert.Null(store.Load().InstalledVersion);
        Assert.Null(store.Load().RestartedFor);
    }

    // ---- found in the code review

    [Fact]
    public async Task A_local_alpha_build_with_the_releases_numbers_is_replaced_by_the_release()
    {
        // "1.0.7-alpha.3" has assembly version 1.0.7.0, like release 1.0.7: only the file itself tells them apart.
        var source = new FakeSource { Release = ReleaseOf(BuiltVersion) };
        var (updater, store, target) = Make(source, BuiltVersion + "-alpha.3");
        File.WriteAllBytes(target, File.ReadAllBytes(BuiltDll).Concat(new byte[] { 0, 0, 0, 0 }).ToArray());
        Assert.True(PluginVersion.From(System.Reflection.AssemblyName.GetAssemblyName(target).Version!).SameNumbers(BuiltVersion));

        Assert.Equal(UpdateOutcome.Installed, await updater.Run(true, false, null, CancellationToken.None));

        Assert.Equal(1, source.Downloads);
        Assert.Equal(File.ReadAllBytes(BuiltDll), File.ReadAllBytes(target));
        Assert.Equal(UpdateOutcome.WaitingForRestart, await updater.Run(true, false, null, CancellationToken.None));
        Assert.Equal(1, source.Downloads);
    }

    [Fact]
    public async Task Only_this_repositorys_own_download_and_release_page_are_used()
    {
        var release = ReleaseOf(BuiltVersion);
        release.assets[0].browser_download_url = "https://example.invalid/Emby.Subremover.dll";
        release.html_url = "javascript:alert(1)";
        var source = new FakeSource { Release = release };
        var (updater, store, target) = Make(source, Older.ToString());

        Assert.Equal(UpdateOutcome.Failed, await updater.Run(true, false, null, CancellationToken.None));

        Assert.Equal(0, source.Downloads);
        Assert.Equal("the old plugin", File.ReadAllText(target));
        Assert.Contains("download address isn't under https://github.com/Snake98bm/Emby.Subremover/releases/download/", store.Load().Problem);
        Assert.Null(store.Load().ReleaseUrl);
    }

    [Fact]
    public async Task A_download_that_stalls_is_given_up_on_and_the_plugin_is_left_alone()
    {
        var source = new FakeSource { Release = ReleaseOf(BuiltVersion), Delay = TimeSpan.FromMinutes(5) };
        var (updater, store, target) = Make(source, Older.ToString());
        updater.DownloadTimeLimit = TimeSpan.FromMilliseconds(100);

        Assert.Equal(UpdateOutcome.Failed, await updater.Run(true, false, null, CancellationToken.None));

        Assert.Contains("took longer than", store.Load().Problem);
        Assert.Equal("the old plugin", File.ReadAllText(target));
    }

    [Fact]
    public async Task Stopping_the_task_during_the_download_stops_it_without_a_problem_note()
    {
        var source = new FakeSource { Release = ReleaseOf(BuiltVersion), Delay = TimeSpan.FromMinutes(5) };
        var (updater, store, target) = Make(source, Older.ToString());
        using var stop = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.Run(true, false, null, stop.Token));

        Assert.Null(store.Load().Problem);
        Assert.Equal("the old plugin", File.ReadAllText(target));
    }

    [Fact]
    public void A_problem_shows_even_while_an_installed_version_waits_for_the_restart()
    {
        var state = new UpdateState
        {
            LastCheckUtc = DateTime.UtcNow, LatestVersion = BuiltVersion.ToString(), InstalledVersion = BuiltVersion.ToString(),
            Problem = "Emby was restarted for it, but the old one is still running."
        };
        var (text, status, _) = Updater.Describe(state, Older, true);
        Assert.StartsWith("Emby was restarted for it", text);
        Assert.Contains($"v{BuiltVersion} is in the plugins folder; running v{Older}.", text);
        Assert.Equal(ItemStatus.Failed, status);
    }

    [Fact]
    public void The_state_file_is_replaced_whole_and_survives_many_saves()
    {
        var store = new UpdateStateStore(Path.Combine(_dir, "config"), new TestJsonSerializer());
        for (var i = 0; i < 20; i++) store.Save(new UpdateState { LatestVersion = "1.0." + i });
        Assert.Equal("1.0.19", store.Load().LatestVersion);
        Assert.Equal(new[] { UpdateStateStore.FileName }, Directory.GetFiles(Path.Combine(_dir, "config")).Select(Path.GetFileName));
    }

    [Fact]
    public void Before_the_first_check_the_page_says_so()
    {
        var (text, status, available) = Updater.Describe(new UpdateState(), BuiltVersion, true);
        Assert.Equal($"Running v{BuiltVersion}. Not checked for updates yet; it checks once a day.", text);
        Assert.Equal(ItemStatus.None, status);
        Assert.Null(available);
    }

    // ---- settings

    [Fact]
    public void Updates_install_by_default_restarting_is_opt_in_and_both_sit_under_Updates_on_the_Run_tab()
    {
        Assert.True(new PluginOptions().AutoUpdate);
        Assert.False(new PluginOptions().RestartForUpdate);
        var run = SettingsLayout.Tabs.Single(t => t.Id == SettingsTabId.Run).Entries.Select(e => e.Item2).ToList();
        var heading = run.IndexOf("Updates");
        Assert.Equal(new[] { SettingsLayout.UpdateWidget, nameof(PluginOptions.AutoUpdate), nameof(PluginOptions.RestartForUpdate) },
            run.Skip(heading + 1).Take(3));
        Assert.Equal(nameof(PluginOptions.AutoUpdate), SettingsLayout.Describe(nameof(PluginOptions.RestartForUpdate)).VisibleIf.Field);
    }

    [Fact]
    public void The_update_task_runs_daily_at_half_past_four()
    {
        var trigger = Assert.Single(new ScheduledTasks.UpdateTask(null!, null!, null!, null!).GetDefaultTriggers());
        Assert.Equal(MediaBrowser.Model.Tasks.TaskTriggerInfo.TriggerDaily, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(4.5).Ticks, trigger.TimeOfDayTicks);
    }
}
