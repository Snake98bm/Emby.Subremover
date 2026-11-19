using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class TimingPlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sr-plan-" + Guid.NewGuid().ToString("N"));

    public TimingPlanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string MakeFile(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private (TimingPlan Plan, string Video, string Srt) PlanWithOneShift()
    {
        var video = MakeFile("My Movie (2020).mkv", "video");
        var srt = MakeFile("My Movie (2020).en.srt", "subtitles");
        var sub = new MeasuredSubtitle
        {
            Path = srt, Language = "eng", Cues = 500, Action = TimingAction.Shift,
            Result = new TimingResult { Verdict = TimingVerdict.Shift, OffsetMs = -2300, BestOffsetMs = -2300, Confidence = 14.2, PartOffsetsMs = new long[] { -2300, -2280, -2320 } }
        };
        var plan = new TimingPlan { Signature = "sig" };
        plan.Put(new PlannedVideo
        {
            VideoPath = video, VideoStamp = FileStamp.Of(video), ScannedUtc = DateTime.UtcNow, AudioMinutes = 101,
            Files = new List<PlannedFile> { PlannedFile.From(sub, FileStamp.Of(srt)) }
        });
        return (plan, video, srt);
    }

    [Fact]
    public void An_unchanged_video_is_done_from_the_saved_scan()
    {
        var (plan, video, srt) = PlanWithOneShift();
        var planned = plan.Usable(video, new[] { srt }, MovedVideos.StampOrNull);
        Assert.NotNull(planned);
        var result = planned!.Files[0].Result()!;
        Assert.Equal(TimingVerdict.Shift, result.Verdict);
        Assert.Equal(-2300, result.OffsetMs);
        Assert.Equal("off by +2.30 s", planned.Files[0].Needs);
    }

    [Fact]
    public void A_changed_subtitle_file_is_measured_again()
    {
        var (plan, video, srt) = PlanWithOneShift();
        File.WriteAllText(srt, "subtitles, edited since the scan");
        Assert.Null(plan.Usable(video, new[] { srt }, MovedVideos.StampOrNull));
    }

    [Fact]
    public void A_changed_video_is_measured_again()
    {
        var (plan, video, srt) = PlanWithOneShift();
        File.WriteAllText(video, "another cut of the video");
        Assert.Null(plan.Usable(video, new[] { srt }, MovedVideos.StampOrNull));
    }

    [Fact]
    public void A_new_subtitle_file_since_the_scan_means_measuring_again()
    {
        var (plan, video, srt) = PlanWithOneShift();
        var added = MakeFile("My Movie (2020).fr.srt", "sous-titres");
        Assert.Null(plan.Usable(video, new[] { srt, added }, MovedVideos.StampOrNull));
    }

    [Fact]
    public void The_plan_is_saved_and_read_back_only_with_the_same_settings()
    {
        var (plan, video, srt) = PlanWithOneShift();
        var store = new TimingPlanStore(_dir, new TestJsonSerializer());
        store.Save(plan);

        var back = store.LoadFor("sig");
        var planned = back.Usable(video, new[] { srt }, MovedVideos.StampOrNull);
        Assert.NotNull(planned);
        Assert.Equal(TimingAction.Shift, planned!.Files[0].Action);
        Assert.Equal(new long[] { -2300, -2280, -2320 }, planned.Files[0].PartOffsetsMs);

        Assert.Equal(0, store.LoadFor("other settings").VideoCount());
    }

    [Fact]
    public void Videos_no_longer_in_the_libraries_are_forgotten()
    {
        var (plan, video, _) = PlanWithOneShift();
        plan.KeepOnly(new[] { "elsewhere.mkv" });
        Assert.Null(plan.Get(video));
        Assert.Empty(plan.Videos);
    }

    [Theory]
    [InlineData(-80, -80, -14080, "off by +0.08 s, then jumps to +14.08 s near the end (another cut)")]
    [InlineData(-6700, -80, -80, "off by +6.70 s at the start, then +0.08 s (another cut)")]
    [InlineData(-240, -2300, -3700, "drifts steadily from +0.24 s to +3.70 s (another frame rate or speed)")]
    [InlineData(-40, -860, -120, "off by +0.04 s, +0.86 s in the middle only (another cut)")]
    public void What_a_drifting_file_needs_is_put_in_words(long start, long middle, long end, string needs)
    {
        var result = new TimingResult { Verdict = TimingVerdict.Drifts, PartOffsetsMs = new[] { start, middle, end } };
        Assert.Equal(needs, result.Needs());
    }
}
