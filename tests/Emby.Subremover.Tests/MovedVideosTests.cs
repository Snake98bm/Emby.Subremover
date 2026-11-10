using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class MovedVideosTests
{
    // Forward slashes: file names split the same on Windows and Linux.
    private const string OldMovie = "/mnt/old/Movies/My Movie (2020)/My Movie (2020).mkv";
    private const string NewMovie = "/mnt/new/Movies/My Movie (2020)/My Movie (2020).mkv";
    private static readonly FileStamp Stamp = new() { Size = 1000, LastWriteTicksUtc = 42 };

    // The files on the share now, all with the same size and date unless told otherwise.
    private static List<MovedVideos.Move> Reconnect(SubremoverState state, string[] current, string[] alsoThere = null, FileStamp stamp = null)
    {
        var there = new HashSet<string>(current.Concat(alsoThere ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
        return MovedVideos.Reconnect(state, current, there.Contains, p => there.Contains(p) ? stamp ?? Stamp : null);
    }

    [Fact]
    public void A_done_video_moved_with_the_same_size_and_date_keeps_its_record()
    {
        var state = new SubremoverState();
        state.Processed[OldMovie] = Stamp;

        var moves = Reconnect(state, new[] { NewMovie });

        var move = Assert.Single(moves);
        Assert.Equal((OldMovie, NewMovie), (move.From, move.To));
        Assert.True(state.Processed.ContainsKey(NewMovie));
        Assert.False(state.Processed.ContainsKey(OldMovie));
    }

    [Fact]
    public void A_copy_or_a_different_file_with_the_same_name_is_not_taken_for_a_move()
    {
        // Still at the old path too: a copy.
        var copied = new SubremoverState();
        copied.Processed[OldMovie] = Stamp;
        Assert.Empty(Reconnect(copied, new[] { NewMovie }, alsoThere: new[] { OldMovie }));

        // Another size or date: another file.
        var other = new SubremoverState();
        other.Processed[OldMovie] = Stamp;
        Assert.Empty(Reconnect(other, new[] { NewMovie }, stamp: new FileStamp { Size = 5, LastWriteTicksUtc = 42 }));
        Assert.True(other.Processed.ContainsKey(OldMovie));
    }

    [Fact]
    public void A_backup_next_to_the_video_follows_it_when_it_moved_along()
    {
        var state = new SubremoverState();
        state.Backups.Add(new BackupEntry { OriginalPath = OldMovie, BackupPath = OldMovie + BackupPaths.SideBySideSuffix });

        Assert.Single(Reconnect(state, new[] { NewMovie }, alsoThere: new[] { NewMovie + BackupPaths.SideBySideSuffix }));
        Assert.Equal(NewMovie, state.Backups[0].OriginalPath);
        Assert.Equal(NewMovie + BackupPaths.SideBySideSuffix, state.Backups[0].BackupPath);

        // The backup didn't come along: left as it was.
        var lost = new SubremoverState();
        lost.Backups.Add(new BackupEntry { OriginalPath = OldMovie, BackupPath = OldMovie + BackupPaths.SideBySideSuffix });
        Assert.Empty(Reconnect(lost, new[] { NewMovie }));
        Assert.Equal(OldMovie, lost.Backups[0].OriginalPath);
    }

    [Fact]
    public void A_backup_in_the_backup_folder_stays_and_goes_back_to_the_new_path()
    {
        const string kept = "/backups/Movies/My Movie (2020)/My Movie (2020).mkv";
        var state = new SubremoverState();
        state.Backups.Add(new BackupEntry { OriginalPath = OldMovie, BackupPath = kept });

        Assert.Single(Reconnect(state, new[] { NewMovie }, alsoThere: new[] { kept }));
        Assert.Equal(NewMovie, state.Backups[0].OriginalPath);
        Assert.Equal(kept, state.Backups[0].BackupPath);
    }

    [Fact]
    public void A_restored_video_stays_left_alone_after_a_move()
    {
        var state = new SubremoverState();
        state.MarkRestored(OldMovie);

        Assert.Single(Reconnect(state, new[] { NewMovie }));
        Assert.True(state.IsRestored(NewMovie));
        Assert.False(state.IsRestored(OldMovie));
    }

    [Fact]
    public void Without_a_size_and_date_to_go_by_two_videos_of_the_same_name_are_left_alone()
    {
        const string twin = "/mnt/new/Kids/My Movie (2020)/My Movie (2020).mkv";
        var state = new SubremoverState();
        state.MarkRestored(OldMovie);

        Assert.Empty(Reconnect(state, new[] { NewMovie, twin }));
        Assert.True(state.IsRestored(OldMovie));
    }

    [Fact]
    public void Subtitle_files_made_for_a_moved_video_are_still_known_as_ours()
    {
        var oldFolder = Path.GetDirectoryName(OldMovie)!;
        var newFolder = Path.GetDirectoryName(NewMovie)!;
        var state = new SubremoverState();
        state.Processed[OldMovie] = Stamp;
        state.MadeFiles.Add(Path.Combine(oldFolder, "My Movie (2020).eng.srt"));
        state.MadeFiles.Add(Path.Combine(oldFolder, "My Movie (2020).pgs.eng.srt"));
        state.MadeFiles.Add(Path.Combine(oldFolder, "Other Movie.eng.srt"));
        state.OcrFiles[Path.Combine(oldFolder, "My Movie (2020).pgs.eng.srt")] = OcrVersion.Record(OcrModels.Both);

        Reconnect(state, new[] { NewMovie });

        // Which OCR made the file moves with it.
        Assert.True(state.OcrFileIsCurrent(Path.Combine(newFolder, "My Movie (2020).pgs.eng.srt"), OcrModels.Both));
        Assert.Single(state.OcrFiles);

        Assert.Contains(Path.Combine(newFolder, "My Movie (2020).eng.srt"), state.MadeFiles);
        Assert.Contains(Path.Combine(newFolder, "My Movie (2020).pgs.eng.srt"), state.MadeFiles);
        Assert.Contains(Path.Combine(oldFolder, "Other Movie.eng.srt"), state.MadeFiles);
        Assert.Equal(3, state.MadeFiles.Count);
    }

    [Fact]
    public void A_test_run_only_moves_its_own_copy_of_the_backups()
    {
        var state = new SubremoverState();
        state.Backups.Add(new BackupEntry { OriginalPath = OldMovie, BackupPath = OldMovie + BackupPaths.SideBySideSuffix });
        var preview = state.PreviewFor(state.SettingsSignature, recheckEverything: false);

        Reconnect(preview, new[] { NewMovie }, alsoThere: new[] { NewMovie + BackupPaths.SideBySideSuffix });

        Assert.Equal(NewMovie, preview.Backups[0].OriginalPath);
        Assert.Equal(OldMovie, state.Backups[0].OriginalPath);
    }
}
