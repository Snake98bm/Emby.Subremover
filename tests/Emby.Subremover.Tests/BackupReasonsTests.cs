using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class BackupReasonsTests
{
    private static BackupEntry Backup(params string[] changes) => new() { OriginalPath = @"C:\Media\My Movie (2020).mkv", Changes = changes.ToList() };

    [Fact]
    public void Reasons_come_from_the_notes()
    {
        Assert.Equal(new[] { "tracks", "default" },
            BackupReasons.Of(Backup("Removed 3 subtitles: French, Spanish (2) · default now English")));
        Assert.Equal(new[] { "extracted", "ocr", "language" },
            BackupReasons.Of(Backup("Extracted .eng.srt · made .pgs.eng.srt (OCR, both models) · language written into 2 tracks")));
        Assert.Equal(new[] { "ads" }, BackupReasons.Of(Backup(string.Format(SubtitleAdFiles.BackupNote, 2, "s"))));
        Assert.Equal(new[] { "timing" }, BackupReasons.Of(Backup(string.Format(SubtitleTimingFiles.ShiftNote, "+2.00 s"))));
        Assert.Equal(new[] { "deleted" }, BackupReasons.Of(Backup(string.Format(SubtitleTimingFiles.BetterCopyNote, "drifts", "a.en.srt"))));
        Assert.Equal(new[] { "deleted" }, BackupReasons.Of(Backup(string.Format(SubtitleTimingFiles.DeleteNote, "drifts", "a.en.srt"))));
        Assert.Equal(new[] { BackupReasons.UnrecordedValue }, BackupReasons.Of(new BackupEntry { OriginalPath = "x.mkv" }));
    }

    [Fact]
    public void Filter_keeps_the_backups_with_that_reason_and_counts_leave_out_unused_ones()
    {
        var ads = Backup(string.Format(SubtitleAdFiles.BackupNote, 1, ""));
        var timing = Backup(string.Format(SubtitleTimingFiles.ShiftNote, "-1.00 s"));
        var video = Backup("Removed 1 subtitle: French");
        var all = new[] { ads, timing, video };

        Assert.Equal(new[] { ads }, BackupReasons.Filter(all, "ads"));
        Assert.Equal(all, BackupReasons.Filter(all, "any"));
        Assert.Equal(all, BackupReasons.Filter(all, null));
        Assert.Equal(all, BackupReasons.Filter(all, "no such reason"));

        var counts = BackupReasons.Counts(all);
        Assert.Equal(new[] { "tracks", "ads", "timing" }, counts.Select(c => c.Value));
        Assert.All(counts, c => Assert.Equal(1, c.Count));
    }

    [Theory]
    [InlineData(1153, 49, "1,153 videos and 49 subtitle files have a backup.")]
    [InlineData(1, 0, "1 video has a backup.")]
    [InlineData(0, 1, "1 subtitle file has a backup.")]
    [InlineData(0, 3, "3 subtitle files have a backup.")]
    [InlineData(0, 0, "No backups.")]
    public void Backup_count_splits_videos_and_subtitle_files(int videos, int subtitles, string text)
    {
        Assert.Equal(text, Plugin.BackupCountText(videos, subtitles));
    }
}

public class DeleteConfirmTests
{
    [Theory]
    [InlineData("Delete", true)]
    [InlineData("delete", true)]
    [InlineData("  DELETE ", true)]
    [InlineData("dELeTe", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("yes", false)]
    [InlineData("deleted", false)]
    public void Backups_are_deleted_only_after_typing_delete(string typed, bool ok)
    {
        Assert.Equal(ok, Plugin.IsDeleteConfirmed(typed));
    }
}
