using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class WayOffSubtitlesTests
{
    private static MeasuredSubtitle Drifting(string language, long start, long middle, long end, string? special = null) => new()
    {
        Path = $"My Movie (2020).{language}.srt",
        Language = language,
        Special = special,
        Cues = 400,
        Result = new TimingResult { Verdict = TimingVerdict.Drifts, Confidence = 12, PartOffsetsMs = new[] { start, middle, end } }
    };

    private static MeasuredSubtitle NoMatch(double score) => new()
    {
        Path = "My Movie (2020).en.srt", Language = "eng", Cues = 400,
        Result = new TimingResult { Verdict = TimingVerdict.Unsure, Confidence = score, BestOffsetMs = 62000 }
    };

    private static TimingAction Decided(MeasuredSubtitle sub, WayOffSubtitles setting, string audio = "eng", bool untaggedIsEnglish = false)
    {
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { sub }, true, true, setting, audio, untaggedIsEnglish);
        return sub.Action;
    }

    [Fact]
    public void The_only_English_file_far_off_in_most_of_the_video_goes()
    {
        var sub = Drifting("eng", -3000, -9000, -14000);
        Assert.Equal(TimingAction.Delete, Decided(sub, WayOffSubtitles.MoreThan2Seconds));
        Assert.True(sub.WayOff);
    }

    [Fact]
    public void Off_never_deletes_the_only_file()
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -3000, -9000, -14000), WayOffSubtitles.Off));
    }

    [Fact]
    public void A_file_right_for_most_of_the_video_that_jumps_near_the_end_goes()
    {
        Assert.Equal(TimingAction.Delete, Decided(Drifting("eng", -80, -80, -14080), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.Delete, Decided(Drifting("eng", 7080, 0, 0), WayOffSubtitles.MoreThan2Seconds));
    }

    [Fact]
    public void A_small_drift_stays()
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -80, -860, -120), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -820, -1520, -1560), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -80, -80, -4080), WayOffSubtitles.MoreThan5Seconds));
    }

    [Fact]
    public void Careful_needs_more_than_five_seconds()
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -2500, -3500, -4500), WayOffSubtitles.MoreThan5Seconds));
        Assert.Equal(TimingAction.Delete, Decided(Drifting("eng", -2500, -6500, -9500), WayOffSubtitles.MoreThan5Seconds));
    }

    [Theory]
    [InlineData("und")]
    [InlineData("fre")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_when_the_audio_is_English(string? audio)
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -3000, -9000, -14000), WayOffSubtitles.MoreThan2Seconds, audio!));
    }

    [Theory]
    [InlineData("und")]
    [InlineData("")]
    [InlineData(null)]
    public void Untagged_audio_counts_as_English_when_asked(string? audio)
    {
        Assert.Equal(TimingAction.Delete, Decided(Drifting("eng", -3000, -9000, -14000), WayOffSubtitles.MoreThan2Seconds, audio!, untaggedIsEnglish: true));
    }

    [Fact]
    public void Untagged_audio_setting_never_counts_other_languages()
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -3000, -9000, -14000), WayOffSubtitles.MoreThan2Seconds, "fre", untaggedIsEnglish: true));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("", -3000, -9000, -14000), WayOffSubtitles.MoreThan2Seconds, "und", untaggedIsEnglish: true));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -3000, -9000, -14000), WayOffSubtitles.Off, "und", untaggedIsEnglish: true));
    }

    [Fact]
    public void Only_English_subtitle_files()
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("fre", -3000, -9000, -14000), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("", -3000, -9000, -14000), WayOffSubtitles.MoreThan2Seconds));
    }

    [Fact]
    public void Sdh_counts_but_commentary_and_forced_never_go()
    {
        Assert.Equal(TimingAction.Delete, Decided(Drifting("eng", -3000, -9000, -14000, "SDH"), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -3000, -9000, -14000, "commentary"), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.LeaveAlone, Decided(Drifting("eng", -3000, -9000, -14000, "forced"), WayOffSubtitles.MoreThan2Seconds));
    }

    [Fact]
    public void A_file_that_does_not_match_the_speech_only_goes_with_the_most_setting()
    {
        Assert.Equal(TimingAction.LeaveAlone, Decided(NoMatch(2.4), WayOffSubtitles.MoreThan2Seconds));
        Assert.Equal(TimingAction.Delete, Decided(NoMatch(2.4), WayOffSubtitles.MoreThan2SecondsOrNoMatch));
        Assert.Equal(TimingAction.LeaveAlone, Decided(NoMatch(3.6), WayOffSubtitles.MoreThan2SecondsOrNoMatch));
        Assert.Equal(TimingAction.LeaveAlone, Decided(NoMatch(0), WayOffSubtitles.MoreThan2SecondsOrNoMatch));
    }

    [Fact]
    public void A_file_that_can_be_moved_is_moved_not_deleted()
    {
        var sub = new MeasuredSubtitle
        {
            Path = "My Movie (2020).en.srt", Language = "eng", Cues = 400,
            Result = new TimingResult { Verdict = TimingVerdict.Shift, OffsetMs = -9000, Confidence = 15, PartOffsetsMs = new long[] { -9000, -9000, -9000 } }
        };
        Assert.Equal(TimingAction.Shift, Decided(sub, WayOffSubtitles.MoreThan2SecondsOrNoMatch));
    }
}
