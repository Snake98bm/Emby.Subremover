using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public sealed class FailedVideosTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subremover-failed-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private const string Video = @"\\nas\Emby\Movies\Alien (1979)\Alien (1979).mkv";
    private static readonly DateTime Now = new(2026, 11, 27, 11, 0, 0, DateTimeKind.Utc);
    private static FileStamp Stamp(long size = 1000) => new() { Size = size, LastWriteTicksUtc = 42 };

    [Fact]
    public void Stops_trying_after_two_failed_runs_by_default()
    {
        Assert.Equal(2, new PluginOptions().MaxFailedAttempts);
        var state = new SubremoverState();

        FailedVideos.Record(state, Video, Stamp(), "duration changed", "subremover-1.txt", Now);
        Assert.False(FailedVideos.ShouldSkip(state, Video, Stamp(), 2));

        FailedVideos.Record(state, Video, Stamp(), "duration changed", "subremover-2.txt", Now.AddDays(1));
        Assert.True(FailedVideos.ShouldSkip(state, Video, Stamp(), 2));
        Assert.True(FailedVideos.ShouldSkip(state, Video.ToUpperInvariant(), Stamp(), 2));
        Assert.False(FailedVideos.ShouldSkip(state, Video, Stamp(), 3));
        Assert.False(FailedVideos.ShouldSkip(state, Video, Stamp(), 0)); // 0 never stops
    }

    [Fact]
    public void A_changed_file_is_tried_again_and_its_count_starts_over()
    {
        var state = new SubremoverState();
        FailedVideos.Record(state, Video, Stamp(), "x", null, Now);
        FailedVideos.Record(state, Video, Stamp(), "x", null, Now);
        Assert.False(FailedVideos.ShouldSkip(state, Video, Stamp(2000), 2));

        var record = FailedVideos.Record(state, Video, Stamp(2000), "y", null, Now.AddDays(2));
        Assert.Equal(1, record.Count);
        Assert.Equal("y", record.LastReason());
    }

    [Fact]
    public void Retry_forgets_the_failures()
    {
        var state = new SubremoverState();
        FailedVideos.Record(state, Video, Stamp(), "x", null, Now);
        FailedVideos.Record(state, Video, Stamp(), "x", null, Now);

        Assert.True(FailedVideos.Forget(state, Video));
        Assert.False(FailedVideos.ShouldSkip(state, Video, Stamp(), 2));
        Assert.False(FailedVideos.Forget(state, Video));
    }

    [Fact]
    public void Only_the_newest_attempts_are_kept_but_the_count_goes_on()
    {
        var state = new SubremoverState();
        FailedVideo record = null;
        for (var i = 0; i < 15; i++) record = FailedVideos.Record(state, Video, Stamp(), "run " + i, null, Now.AddDays(i));

        Assert.Equal(15, record!.Count);
        Assert.Equal(FailedVideos.KeepAttempts, record.Attempts.Count);
        Assert.Equal("run 14", record.LastReason());
        Assert.Equal(Now, record.FirstUtc);
    }

    [Theory]
    [InlineData(7993, 8134, true)]   // 25 Nov 2026: the share or ffmpeg, not the videos
    [InlineData(3, 124, false)]      // a normal night
    [InlineData(24, 24, false)]      // a few videos, all bad
    [InlineData(30, 1000, false)]
    public void A_run_where_nearly_everything_failed_does_not_count(int failed, int looked, bool wholeRun)
    {
        Assert.Equal(wholeRun, FailedVideos.LooksLikeWholeRunProblem(failed, looked));
    }

    [Fact]
    public void Re_check_every_video_forgets_the_failures_and_test_mode_sees_them()
    {
        var state = new SubremoverState();
        FailedVideos.Record(state, Video, Stamp(), "x", null, Now);

        var preview = state.PreviewFor("sig", recheckEverything: false);
        Assert.True(preview.Failures.ContainsKey(Video));
        preview.Failures.Clear();
        Assert.True(state.Failures.ContainsKey(Video)); // a copy

        state.ForgetEverything();
        Assert.Empty(state.Failures);
    }

    [Fact]
    public void Failures_survive_saving_and_loading()
    {
        var store = new StateStore(_dir, new TestJsonSerializer());
        var state = new SubremoverState();
        FailedVideos.Record(state, Video, Stamp(), "the rewritten file failed verification", "subremover-20261127-030000.txt", Now);
        store.Save(state);

        var loaded = store.Load();
        var record = loaded.Failures[Video.ToLowerInvariant()];
        Assert.Equal(1, record.Count);
        Assert.Equal("subremover-20261127-030000.txt", record.Attempts.Single().LogFile);
        Assert.True(record.Stamp.Matches(Stamp()));
    }

    [Fact]
    public void Old_state_files_without_failures_load()
    {
        File.WriteAllText(Path.Combine(_dir, "Emby.Subremover.state.json"), "{\"Processed\":{},\"Backups\":[]}");
        var loaded = new StateStore(_dir, new TestJsonSerializer()).Load();
        Assert.NotNull(loaded.Failures);
        Assert.False(FailedVideos.ShouldSkip(loaded, Video, Stamp(), 2));
    }

    [Fact]
    public void Long_reasons_are_shortened_to_one_line()
    {
        var reason = "ffmpeg exit 1:\r\n" + new string('x', 1000);
        var shown = FailedVideos.Shorten(reason, 100);
        Assert.DoesNotContain("\n", shown);
        Assert.Equal(101, shown.Length);
        Assert.EndsWith("…", shown);
    }

    [Fact]
    public void Failure_log_is_newest_first_and_served_by_name()
    {
        var log = new FailureLog(_dir);
        log.Write("FAILED   a", new DateTime(2026, 11, 27, 3, 0, 0));
        log.Write("FAILED   b\nwith a newline", new DateTime(2026, 11, 28, 3, 0, 0));

        var lines = log.ReadNewestFirst().Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("2026-11-28 03:00:00  FAILED   b with a newline", lines[0]);
        Assert.EndsWith("FAILED   a", lines[1]);
        // A .log, so it's never listed (or rotated away) as one of the 30 run logs.
        Assert.DoesNotContain(FailureLog.FileName, RunLog.ListFiles(_dir));
    }
}
