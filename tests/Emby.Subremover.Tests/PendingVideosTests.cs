using Emby.Subremover.Core;
using Emby.Subremover.ScheduledTasks;
using Xunit;

namespace Emby.Subremover.Tests;

public class PendingVideosTests
{
    [Fact]
    public void Take_with_nothing_waiting_is_null()
    {
        Assert.Null(new PendingVideos().Take());
    }

    [Fact]
    public void Take_hands_over_the_batch_once()
    {
        var pending = new PendingVideos();
        pending.Add(@"D:\Movies\My Movie (2020)\My Movie (2020).mkv");
        pending.Add(@"D:\TV\My Show\Season 1\My Show - S01E02.mkv");

        var batch = pending.Take();

        Assert.Equal(2, batch.Count);
        Assert.Equal(0, pending.Count);
        Assert.Null(pending.Take());
    }

    [Fact]
    public void The_same_video_added_twice_is_checked_once()
    {
        var pending = new PendingVideos();
        pending.Add(@"D:\Movies\My Movie (2020)\My Movie (2020).mkv");
        pending.Add(@"d:\movies\my movie (2020)\MY MOVIE (2020).MKV");

        Assert.Single(pending.Take());
    }

    [Fact]
    public void Handing_a_batch_back_keeps_it_for_the_next_run()
    {
        var pending = new PendingVideos();
        pending.Add(@"D:\Movies\My Movie (2020)\My Movie (2020).mkv");
        var batch = pending.Take();

        pending.AddRange(batch);
        pending.AddRange(null);

        Assert.Single(pending.Take());
    }

    [Fact]
    public void A_run_for_new_videos_ignores_the_per_run_limit()
    {
        var options = new PluginOptions { MaxItemsPerRun = 5 };

        Assert.Equal(0, RemoveSubtitlesTask.LimitFor(options, newVideosOnly: true));
        Assert.Equal(5, RemoveSubtitlesTask.LimitFor(options, newVideosOnly: false));
    }
}
