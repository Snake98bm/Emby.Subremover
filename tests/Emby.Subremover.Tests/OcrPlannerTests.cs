using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class OcrPlannerTests
{
    private const string Video = @"D:\Movies\Heat (1995)\Heat (1995).mkv";

    private static PlannerSettings Ocr(string languages) => new()
    {
        KeepLanguages = PlannerSettings.ParseLanguages(languages),
        OcrPictureSubtitles = true
    };

    private static string Name(OcrJob job) => Path.GetFileName(job.OutputPath.Replace('\\', Path.DirectorySeparatorChar));

    [Fact]
    public void Kept_picture_tracks_get_an_ocr_file_marked_with_their_type()
    {
        var plan = SubtitlePlanner.Plan(Movie(
            Sub(2, "eng", codec: "hdmv_pgs_subtitle"),
            Sub(3, "eng", codec: "dvd_subtitle", forced: true),
            Sub(4, "eng", codec: "subrip")), Ocr("eng"), Video);

        Assert.Equal(new[] { "Heat (1995).pgs.eng.srt", "Heat (1995).dvd.eng.forced.srt" }, plan.Ocr.Select(Name));
        Assert.All(plan.Ocr, j => Assert.Equal("eng", j.TesseractLanguage));
        Assert.Equal(3, plan.Keep.Count);
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void Removed_picture_tracks_are_not_ocrd()
    {
        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "fre", codec: "hdmv_pgs_subtitle")), Ocr("eng"), Video);
        Assert.Empty(plan.Ocr);
        Assert.Single(plan.Remove);
    }

    [Fact]
    public void Ocr_is_off_unless_enabled()
    {
        var settings = Ocr("eng");
        settings.OcrPictureSubtitles = false;
        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle")), settings, Video);
        Assert.Empty(plan.Ocr);
        Assert.False(plan.HasWork);
    }

    [Theory]
    [InlineData("ger", "deu")]
    [InlineData("fre", "fra")]
    [InlineData("chi", "chi_sim")]
    [InlineData("spa", "spa")]
    [InlineData("und", "eng")]
    public void Uses_tesseract_language_names(string trackLanguage, string tesseract)
    {
        var settings = Ocr(trackLanguage);
        var plan = SubtitlePlanner.Plan(Movie(Sub(2, trackLanguage, codec: "hdmv_pgs_subtitle")), settings, Video);
        Assert.Equal(tesseract, Assert.Single(plan.Ocr).TesseractLanguage);
    }

    [Fact]
    public void Languages_tesseract_cannot_read_are_skipped()
    {
        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "qaa", codec: "hdmv_pgs_subtitle")), Ocr("qaa"), Video);
        Assert.Empty(plan.Ocr);
    }

    [Fact]
    public void An_existing_ocr_file_is_kept_unless_replace_is_chosen()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-ocr-plan-").FullName;
        try
        {
            var video = Path.Combine(dir, "Movie.mkv");
            File.WriteAllText(Path.Combine(dir, "Movie.pgs.eng.srt"), "old");
            File.WriteAllText(Path.Combine(dir, "Movie.eng.srt"), "unrelated sidecar");
            var probe = Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle"), Sub(3, "eng", codec: "hdmv_pgs_subtitle", forced: true));

            var keep = SubtitlePlanner.Plan(probe, Ocr("eng"), video);
            Assert.Equal(new[] { "Movie.pgs.eng.forced.srt" }, keep.Ocr.Select(Name));

            var settings = Ocr("eng");
            settings.OcrReplaceExisting = true;
            var replace = SubtitlePlanner.Plan(probe, settings, video);
            Assert.Equal(new[] { "Movie.pgs.eng.srt", "Movie.pgs.eng.forced.srt" }, replace.Ocr.Select(Name));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Plan_log_lists_every_kind_of_work_including_ocr()
    {
        var settings = Ocr("eng");
        settings.ExtractTextSubtitles = true;
        var probe = Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle"), Sub(3, "eng"), Sub(4, "fre", title: "Weird {0} title"));

        var text = MediaProcessor.DescribePlan(Video, SubtitlePlanner.Plan(probe, settings, Video), dryRun: true);

        Assert.Contains("[TEST MODE - no changes]", text);
        Assert.Contains("keep:    #2 eng hdmv_pgs_subtitle, #3 eng subrip", text);
        Assert.Contains("remove:  #4 fre subrip \"Weird {0} title\"", text);
        Assert.Contains("extract: #3 eng subrip -> Heat (1995).eng.srt", text.Replace('\\', '/').Replace("D:/Movies/Heat (1995)/", ""));
        Assert.Contains("ocr:     #2 eng hdmv_pgs_subtitle -> Heat (1995).pgs.eng.srt", text.Replace('\\', '/').Replace("D:/Movies/Heat (1995)/", ""));
    }

    [Fact]
    public void Signature_changes_with_settings_but_not_language_order()
    {
        var a = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng,spa") };
        var b = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("spa, eng") };
        var c = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng,spa"), OcrPictureSubtitles = true };

        Assert.Equal(a.Signature(), b.Signature());
        Assert.NotEqual(a.Signature(), c.Signature());
    }
}
