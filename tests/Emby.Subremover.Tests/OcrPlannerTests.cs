using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class OcrPlannerTests
{
    private const string Video = @"D:\Movies\My Movie (2020)\My Movie (2020).mkv";

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
            Sub(3, "eng", codec: "dvd_subtitle", forced: true)), Ocr("eng"), Video);

        Assert.Equal(new[] { "My Movie (2020).pgs.eng.srt", "My Movie (2020).dvd.eng.forced.srt" }, plan.Ocr.Select(Name));
        Assert.All(plan.Ocr, j => Assert.Equal("eng", j.TesseractLanguage));
        Assert.Equal(2, plan.Keep.Count);
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
        var settings = Ocr("eng, spa");
        settings.ExtractTextSubtitles = true;
        var probe = Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle"), Sub(3, "spa"), Sub(4, "fre", title: "Weird {0} title"));

        var text = MediaProcessor.DescribePlan(Video, SubtitlePlanner.Plan(probe, settings, Video), dryRun: true);

        Assert.Contains("[TEST MODE - no changes]", text);
        Assert.Contains("keep:    #2 eng hdmv_pgs_subtitle, #3 spa subrip", text);
        Assert.Contains("remove:  #4 fre subrip \"Weird {0} title\"", text);
        Assert.Contains("extract: #3 spa subrip -> My Movie (2020).spa.srt", text.Replace('\\', '/').Replace("D:/Movies/My Movie (2020)/", ""));
        Assert.Contains("ocr:     #2 eng hdmv_pgs_subtitle -> My Movie (2020).pgs.eng.srt", text.Replace('\\', '/').Replace("D:/Movies/My Movie (2020)/", ""));
    }

    [Theory]
    [InlineData(false, false, true)]   // a regular text track in the language: extract that instead
    [InlineData(true, false, false)]   // only a forced text track: the full picture track still needs OCR
    [InlineData(false, true, false)]   // a regular text track doesn't replace forced pictures
    [InlineData(true, true, true)]     // forced pictures are covered by a forced text track
    public void Picture_tracks_are_only_ocrd_when_no_text_track_in_that_language_stays(bool textForced, bool pictureForced, bool skipped)
    {
        var probe = Movie(Sub(2, "eng", forced: textForced), Sub(3, "eng", codec: "hdmv_pgs_subtitle", forced: pictureForced), Sub(4, "fre", codec: "hdmv_pgs_subtitle"));

        var plan = SubtitlePlanner.Plan(probe, Ocr("eng, fre"), Video);

        Assert.Equal(skipped ? new[] { 4 } : new[] { 3, 4 }, plan.Ocr.Select(j => j.Stream.index));
    }

    [Fact]
    public void Removed_or_commentary_text_tracks_dont_stop_the_ocr()
    {
        var settings = Ocr("eng");
        settings.RemoveHearingImpaired = true;
        var probe = Movie(Sub(2, "fre"), Sub(3, "eng", title: "Commentary"), Sub(4, "eng", codec: "hdmv_pgs_subtitle"));

        Assert.Equal(new[] { 4 }, SubtitlePlanner.Plan(probe, settings, Video).Ocr.Select(j => j.Stream.index));
    }

    [Fact]
    public void The_first_regular_subtitle_file_can_be_named_default()
    {
        var settings = Ocr("eng, spa");
        settings.ExtractTextSubtitles = true;
        settings.MarkDefaultInName = true;
        var probe = Movie(Sub(2, "und"), Sub(3, "eng", sdh: true), Sub(4, "eng"), Sub(5, "spa"), Sub(6, "fre", codec: "hdmv_pgs_subtitle"));

        var ocr = SubtitlePlanner.Plan(Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle"), Sub(3, "spa")), settings, Video);
        Assert.Equal("My Movie (2020).pgs.eng.default.srt", Name(Assert.Single(ocr.Ocr)));
        Assert.Equal("My Movie (2020).spa.srt", Path.GetFileName(Assert.Single(ocr.Extract).OutputPath.Replace('\\', Path.DirectorySeparatorChar)));

        settings.KeepUndefined = true;
        var names = SubtitlePlanner.Plan(probe, settings, Video).Extract.Select(e => Path.GetFileName(e.OutputPath.Replace('\\', Path.DirectorySeparatorChar)));
        Assert.Equal(new[] { "My Movie (2020).eng.sdh.srt", "My Movie (2020).eng.default.srt", "My Movie (2020).spa.srt" }, names);

        settings.MarkDefaultInName = false;
        Assert.DoesNotContain(SubtitlePlanner.Plan(probe, settings, Video).Extract, e => e.OutputPath.Contains(".default."));
    }

    [Fact]
    public void An_existing_file_keeps_its_name_when_default_marking_is_turned_on_or_off()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-default-name-").FullName;
        try
        {
            var video = Path.Combine(dir, "Movie.mkv");
            File.WriteAllText(Path.Combine(dir, "Movie.eng.srt"), "extracted earlier");
            File.WriteAllText(Path.Combine(dir, "Movie.pgs.fre.default.srt"), "ocr'd earlier");
            var settings = Ocr("eng, fre");
            settings.ExtractTextSubtitles = true;
            settings.MarkDefaultInName = true;

            var marked = SubtitlePlanner.Plan(Movie(Sub(2, "eng")), settings, video);
            Assert.Equal("Movie.eng.srt", Path.GetFileName(Assert.Single(marked.Extract).OutputPath));

            settings.MarkDefaultInName = false;
            var unmarked = SubtitlePlanner.Plan(Movie(Sub(2, "fre", codec: "hdmv_pgs_subtitle")), settings, video);
            Assert.Empty(unmarked.Ocr);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
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

    [Theory]
    [InlineData("spa", null, "spa")]     // the chosen language
    [InlineData(null, 3, "eng")]         // automatic: the language the video flags as default
    [InlineData(null, null, "spa")]      // automatic, nothing flagged: the first in the video
    [InlineData("ger", 3, "eng")]        // chosen language not in this video
    public void The_default_file_goes_to_the_chosen_language(string chosen, int? flagged, string expected)
    {
        var settings = Ocr("eng, spa, ger");
        settings.ExtractTextSubtitles = true;
        settings.MarkDefaultInName = true;
        settings.DefaultLanguage = chosen;
        var probe = Movie(Sub(2, "spa", isDefault: flagged == 2), Sub(3, "eng", sdh: true, isDefault: flagged == 3), Sub(4, "eng"));

        var names = SubtitlePlanner.Plan(probe, settings, Video).Extract.Select(e => Path.GetFileName(e.OutputPath.Replace('\\', Path.DirectorySeparatorChar)));

        Assert.Equal(new[] { $"My Movie (2020).{expected}.default.srt" }, names.Where(n => n.Contains(".default.")));
    }

    [Fact]
    public void Sdh_gets_the_default_file_when_it_is_the_only_track_in_the_language()
    {
        var settings = Ocr("eng, spa");
        settings.ExtractTextSubtitles = true;
        settings.MarkDefaultInName = true;
        settings.DefaultLanguage = "eng";

        string Names(ProbeResult probe) => string.Join(" ", SubtitlePlanner.Plan(probe, settings, Video).Extract
            .Select(e => Path.GetFileName(e.OutputPath.Replace('\\', Path.DirectorySeparatorChar))));

        Assert.Equal("My Movie (2020).spa.srt My Movie (2020).eng.sdh.default.srt", Names(Movie(Sub(2, "spa"), Sub(3, "eng", sdh: true))));
        Assert.Equal("My Movie (2020).eng.sdh.srt My Movie (2020).eng.default.srt", Names(Movie(Sub(2, "eng", sdh: true), Sub(3, "eng"))));
        Assert.Equal("My Movie (2020).eng.forced.srt My Movie (2020).eng.sdh.default.srt", Names(Movie(Sub(2, "eng", forced: true), Sub(3, "eng", sdh: true))));
    }

    [Fact]
    public void Finds_subtitle_files_subremover_didnt_make()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-existing-").FullName;
        try
        {
            var video = Path.Combine(dir, "Movie (2020).mkv");
            foreach (var f in new[] { "Movie (2020).mkv", "Movie (2020).en.srt", "Movie (2020).pgs.eng.srt", "Movie (2020).fre.srt", "Movie (2020).spa.sdh.srt", "Movie (2020).mkv.subremover.bak", "Other.en.srt" })
                File.WriteAllText(Path.Combine(dir, f), "x");

            var found = ExistingSubtitles.Find(video, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(dir, "Movie (2020).fre.srt") });

            Assert.Equal(new Dictionary<string, string> { ["eng"] = "Movie (2020).en.srt", ["spa"] = "Movie (2020).spa.sdh.srt" }, found);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_language_with_an_existing_file_gets_no_file_of_ours_and_loses_the_embedded_default()
    {
        var settings = Ocr("eng, spa");
        settings.ExtractTextSubtitles = true;
        settings.MarkDefaultInName = true;
        settings.MakeRegularDefault = true;
        settings.PreferExistingFiles = true;
        var probe = Movie(Sub(2, "eng", isDefault: true), Sub(3, "eng", codec: "hdmv_pgs_subtitle", forced: true), Sub(4, "spa"));
        var existing = new Dictionary<string, string> { ["eng"] = "My Movie (2020).en.srt" };

        var plan = SubtitlePlanner.Plan(probe, settings, Video, existing);

        Assert.Equal(new[] { 4 }, plan.Extract.Select(e => e.Stream.index));
        Assert.EndsWith("My Movie (2020).spa.srt", plan.Extract[0].OutputPath);
        Assert.Empty(plan.Ocr);
        Assert.Equal(new[] { 2, 3, 4 }, plan.Keep.Select(s => s.index));
        Assert.Equal(new Dictionary<int, bool> { [2] = false }, plan.DefaultChanges());
        Assert.Contains("existing: eng -> My Movie (2020).en.srt", MediaProcessor.DescribePlan(Video, plan, dryRun: true));

        // A track that's only guessed English (files-only) isn't changed in the video for the existing file.
        var guessed = Movie(Sub(2, "und", isDefault: true));
        guessed.streams[2].GuessedLanguage = "eng";
        settings.KeepUndefined = true;
        var untouched = SubtitlePlanner.Plan(guessed, settings, Video, existing);
        Assert.Empty(untouched.DefaultChanges());
        Assert.False(untouched.HasWork);

        settings.PreferExistingFiles = false;
        var ignored = SubtitlePlanner.Plan(probe, settings, Video, existing);
        Assert.Equal(new[] { 2, 4 }, ignored.Extract.Select(e => e.Stream.index));
        Assert.Empty(ignored.DefaultChanges());
    }
}
