using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

// Forum report (1.0.6, CIFS share mounted with uid=/gid=): every changed video logged "could not copy owner/group: chown:
// Operation not permitted", although the new file already had the share's owner and group.
public class UnixOwnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sr-owner-" + Guid.NewGuid().ToString("N"));

    public UnixOwnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    // stat answers with the given owners; chown always fails, as on a CIFS mount.
    private static UnixPermissions.Runner Share(string sourceOwner, string targetOwner, List<string> calls) => (exe, args) =>
    {
        calls.Add(exe == "chown" ? exe + " " + args.Split(' ')[0] : exe);
        if (exe == "stat") return (0, args.Contains("subremover-tmp") ? targetOwner + "\n" : sourceOwner + "\n", "");
        return (1, "", "chown: changing ownership of 'x': Operation not permitted\n");
    };

    [Fact]
    public void Same_owner_and_group_skips_chown_and_says_nothing()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix only"); return; }
        var calls = new List<string>();

        var note = UnixPermissions.CopyFrom(Write("Movie.mkv"), Write("Movie.mkv.subremover-tmp"), Share("1000:1000", "1000:1000", calls));

        Assert.Null(note);
        Assert.DoesNotContain(calls, c => c.StartsWith("chown"));
    }

    [Fact]
    public void A_different_owner_that_chown_cant_set_is_explained()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix only"); return; }
        var calls = new List<string>();

        var note = UnixPermissions.CopyFrom(Write("Movie.mkv"), Write("Movie.mkv.subremover-tmp"), Share("0:0", "1000:1000", calls));

        // By ids, which BusyBox's chown (Emby's Docker image) understands; it has no --reference.
        Assert.Contains("chown 0:0", calls);
        Assert.Equal("the file was updated, but it keeps owner/group 1000:1000 instead of the original's 0:0 " +
                     "(chown: changing ownership of 'x': Operation not permitted)", note);
    }

    [Fact]
    public void Without_stat_it_still_tries_chown()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix only"); return; }
        var calls = new List<string>();
        UnixPermissions.Runner noStat = (exe, args) =>
        {
            calls.Add(exe == "chown" ? exe + " " + args.Substring(0, 12) : exe);
            return exe == "stat" ? (1, "", "stat: illegal option -- c") : (0, "", "");
        };

        Assert.Null(UnixPermissions.CopyFrom(Write("Movie.mkv"), Write("Movie.mkv.subremover-tmp"), noStat));
        Assert.Contains("chown --reference=", calls);
    }

    [Fact]
    public void Real_stat_on_two_of_my_own_files_needs_no_chown()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix only"); return; }
        var calls = new List<string>();
        var real = (UnixPermissions.Runner)typeof(UnixPermissions)
            .GetMethod("RunProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate(typeof(UnixPermissions.Runner));
        UnixPermissions.Runner watched = (exe, args) => { calls.Add(exe); return real(exe, args); };

        Assert.Null(UnixPermissions.CopyFrom(Write("Movie.mkv"), Write("Movie.mkv.subremover-tmp"), watched));
        Assert.Equal(new[] { "stat", "stat" }, calls);
    }
}
