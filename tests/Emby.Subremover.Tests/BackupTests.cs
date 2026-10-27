using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subremover-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData(@"D:\Movies\A\a.mkv", @"D\Movies\A\a.mkv")]
    [InlineData("/mnt/media/a.mkv", "mnt/media/a.mkv")]
    [InlineData(@"\\nas\share\a.mkv", @"nas\share\a.mkv")]
    public void Backup_folder_mirrors_the_original_path(string original, string expected)
    {
        Assert.Equal(expected.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
            BackupPaths.RelativeFromRoot(original));
    }

    [Fact]
    public void NextToOriginal_keeps_a_bak_file_and_restore_puts_it_back()
    {
        var movie = Write("Movie.mkv", "original");
        var temp = Write("Movie.mkv.subremover-tmp", "new");

        var result = BackupManager.Replace(movie, temp, BackupMode.NextToOriginal, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("new", File.ReadAllText(movie));
        Assert.Equal("original", File.ReadAllText(movie + ".subremover.bak"));
        Assert.False(File.Exists(temp));
        Assert.NotNull(result.NewBackup);

        BackupManager.Restore(result.NewBackup);

        Assert.Equal("original", File.ReadAllText(movie));
        Assert.False(File.Exists(movie + ".subremover.bak"));
    }

    [Fact]
    public void An_older_backup_is_never_overwritten()
    {
        var movie = Write("Movie.mkv", "second version");
        Write("Movie.mkv.subremover.bak", "true original");
        var temp = Write("Movie.mkv.subremover-tmp", "third version");

        var result = BackupManager.Replace(movie, temp, BackupMode.NextToOriginal, null);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.NewBackup);
        Assert.Equal("true original", File.ReadAllText(movie + ".subremover.bak"));
        Assert.Equal("third version", File.ReadAllText(movie));
    }

    [Fact]
    public void BackupFolder_copies_the_original_and_restore_brings_it_back()
    {
        var movie = Write(Path.Combine("lib", "Movie.mkv"), "original");
        var temp = Write(Path.Combine("lib", "Movie.mkv.subremover-tmp"), "new");
        var backupRoot = Path.Combine(_dir, "backups");

        var result = BackupManager.Replace(movie, temp, BackupMode.BackupFolder, backupRoot);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith(backupRoot, result.BackupPath);
        Assert.Equal("original", File.ReadAllText(result.BackupPath));
        Assert.Equal("new", File.ReadAllText(movie));

        BackupManager.Restore(result.NewBackup);

        Assert.Equal("original", File.ReadAllText(movie));
        Assert.False(File.Exists(result.BackupPath));
    }

    [Fact]
    public void None_mode_replaces_without_a_backup()
    {
        var movie = Write("Movie.mkv", "original");
        var temp = Write("Movie.mkv.subremover-tmp", "new");

        var result = BackupManager.Replace(movie, temp, BackupMode.None, null);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.BackupPath);
        Assert.Equal("new", File.ReadAllText(movie));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void New_file_gets_the_original_permissions_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions only");
            return;
        }

        var movie = Write("Movie.mkv", "original");
        var temp = Write("Movie.mkv.subremover-tmp", "new");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(movie, mode);
        File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = BackupManager.Replace(movie, temp, BackupMode.NextToOriginal, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(mode, File.GetUnixFileMode(movie));
    }

    [Fact]
    public void A_failed_backup_leaves_the_original_untouched()
    {
        var movie = Write("Movie.mkv", "original");
        var temp = Write("Movie.mkv.subremover-tmp", "new");
        var blocker = Write("not-a-folder", "x");

        var result = BackupManager.Replace(movie, temp, BackupMode.BackupFolder, blocker);

        Assert.False(result.Success);
        Assert.Equal("original", File.ReadAllText(movie));
        Assert.False(File.Exists(temp));
    }
}
