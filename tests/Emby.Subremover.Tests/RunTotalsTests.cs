using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class RunTotalsTests
{
    private const string Video = @"D:\Movies\My Movie (2020)\My Movie (2020).mkv";

    [Fact]
    public void Test_mode_adds_up_what_each_plan_would_do()
    {
        var settings = new PlannerSettings
        {
            KeepLanguages = PlannerSettings.ParseLanguages("eng"),
            ExtractTextSubtitles = true,
            OcrPictureSubtitles = true,
            MarkDefaultInName = true,
            MakeRegularDefault = true
        };
        var totals = new RunTotals();

        totals.AddPlan(SubtitlePlanner.Plan(Movie(Sub(2, "eng", sdh: true, isDefault: true), Sub(3, "eng"), Sub(4, "fre"), Sub(5, "ger")), settings, Video));
        totals.AddPlan(SubtitlePlanner.Plan(Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle")), settings, Video));

        Assert.Equal(1, totals.VideosRewritten);
        Assert.Equal(2, totals.TracksRemoved);
        Assert.Equal(1, totals.DefaultsChanged);
        Assert.Equal(2, totals.SubtitlesExtracted);
        Assert.Equal(1, totals.OcrFiles);
        Assert.Equal(2, totals.DefaultFiles);
    }

    [Fact]
    public void Real_runs_count_what_was_done()
    {
        var totals = new RunTotals();
        totals.AddExtracted(@"D:\m\My Movie (2020).eng.default.srt");
        totals.AddExtracted(@"D:\m\My Movie (2020).eng.sdh.srt");
        totals.AddOcr(@"D:\m\My Movie (2020).pgs.spa.srt");
        totals.OcrFailed++;

        var text = totals.Describe(dryRun: false);

        Assert.StartsWith("Totals:", text);
        Assert.Contains("subtitle files extracted:   2", text);
        Assert.Contains("OCR subtitle files made:    1 (1 failed)", text);
        Assert.Contains("files named .default:       1", text);
        Assert.StartsWith("Totals - what a real run would do:", totals.Describe(dryRun: true));
    }
}
