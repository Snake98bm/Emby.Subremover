using Emby.Subremover.Core;
using Emby.Subremover.ScheduledTasks;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Subremover.Tests;

/// <summary>Fixes from the 1.0.3 code review (1.0.4).</summary>
public class ReviewFix104Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sr-104-" + Guid.NewGuid().ToString("N"));

    public ReviewFix104Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Make(string name, string text = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static MeasuredSubtitle Sub(string name, TimingVerdict verdict, double confidence) => new()
    {
        Path = name, Language = "eng", Cues = 500,
        Result = new TimingResult { Verdict = verdict, Confidence = confidence }
    };

    [Fact]
    public void Files_that_could_not_be_measured_are_never_deleted_as_the_worse_copy()
    {
        // A flat speech track: every file is "no clear match" with confidence 0.
        var a = Sub("Show.en.srt", TimingVerdict.Unsure, 0);
        var b = Sub("Show.eng.srt", TimingVerdict.Unsure, 0);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { a, b }, true, deleteOtherVersions: true);
        Assert.NotEqual(TimingAction.Delete, a.Action);
        Assert.NotEqual(TimingAction.Delete, b.Action);
    }

    [Fact]
    public void An_unmeasurable_copy_is_kept_next_to_a_drifting_one()
    {
        var drifting = Sub("Show.en.srt", TimingVerdict.Drifts, 12);
        drifting.Result.PartOffsetsMs = new long[] { 0, 1500, 3000 };
        var flat = Sub("Show.eng.srt", TimingVerdict.Unsure, 0);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { drifting, flat }, true, deleteOtherVersions: true);
        Assert.NotEqual(TimingAction.Delete, flat.Action);
    }

    [Fact]
    public void A_measured_no_match_copy_still_goes_next_to_a_better_one()
    {
        var drifting = Sub("Show.en.srt", TimingVerdict.Drifts, 12);
        drifting.Result.PartOffsetsMs = new long[] { 0, 1500, 3000 };
        var noMatch = Sub("Show.eng.srt", TimingVerdict.Unsure, 4);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { drifting, noMatch }, true, deleteOtherVersions: true);
        Assert.Equal(TimingAction.Delete, noMatch.Action);
    }

    [Fact]
    public void Another_cut_with_a_longer_name_keeps_its_own_subtitle_files()
    {
        foreach (var f in new[] { "Movie.mkv", "Movie.en.srt", "Movie.Extended.mkv", "Movie.Extended.en.srt", "Movie.Extended.srt" })
            Make(f);
        var theatrical = SubtitleAdFiles.FilesFor(Path.Combine(_dir, "Movie.mkv")).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "Movie.en.srt" }, theatrical);
        var extended = SubtitleAdFiles.FilesFor(Path.Combine(_dir, "Movie.Extended.mkv")).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Movie.Extended.en.srt", "Movie.Extended.srt" }, extended);
    }

    [Fact]
    public void Subtitle_files_are_found_whatever_the_case_of_their_extension()
    {
        Make("Movie.mkv");
        Make("Movie.en.SRT");
        Assert.Equal(new[] { "Movie.en.SRT" }, SubtitleAdFiles.FilesFor(Path.Combine(_dir, "Movie.mkv")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_saved_scan_with_names_differing_only_in_case_is_measured_again_not_an_error()
    {
        var video = Make("Movie.mkv", "video");
        var plan = new TimingPlan { Signature = "sig" };
        var upper = Path.Combine(_dir, "Movie.EN.srt");
        var lower = Make("Movie.en.srt", "subs");
        plan.Put(new PlannedVideo
        {
            VideoPath = video, VideoStamp = FileStamp.Of(video), ScannedUtc = DateTime.UtcNow,
            Files = new List<PlannedFile>
            {
                new() { Path = lower, Stamp = FileStamp.Of(lower), Verdict = TimingVerdict.InSync },
                new() { Path = upper, Stamp = FileStamp.Of(lower), Verdict = TimingVerdict.Shift }
            }
        });
        Assert.Null(plan.Usable(video, new[] { lower, upper }, MovedVideos.StampOrNull));
    }

    [Fact]
    public void Two_run_logs_started_in_the_same_second_get_their_own_files()
    {
        using var first = new RunLog(_dir, "subremover", new NullLogger());
        using var second = new RunLog(_dir, "subremover", new NullLogger());
        Assert.NotEqual(first.FilePath, second.FilePath);
        first.Info("first");
        second.Info("second");
        Assert.True(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));
    }

    [Fact]
    public void A_stopped_fix_starts_over_when_the_way_off_settings_change()
    {
        var options = new PluginOptions { Libraries = "1", DeleteWayOffSubtitles = WayOffSubtitles.Off };
        var before = SubtitleTimingTask.ProgressKey(options);
        options.DeleteWayOffSubtitles = WayOffSubtitles.MoreThan2Seconds;
        Assert.NotEqual(before, SubtitleTimingTask.ProgressKey(options));
        var wayOff = SubtitleTimingTask.ProgressKey(options);
        options.WayOffUntaggedAudioIsEnglish = !options.WayOffUntaggedAudioIsEnglish;
        Assert.NotEqual(wayOff, SubtitleTimingTask.ProgressKey(options));
        var untagged = SubtitleTimingTask.ProgressKey(options);
        options.SkipExtractedTimingCopies = !options.SkipExtractedTimingCopies;
        Assert.NotEqual(untagged, SubtitleTimingTask.ProgressKey(options));
    }
}
