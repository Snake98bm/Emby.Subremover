using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class SubtitlePlannerTests
{
    private const string Video = @"D:\Movies\Heat (1995)\Heat (1995).mkv";

    private static PlannerSettings Keep(string languages) => new() { KeepLanguages = PlannerSettings.ParseLanguages(languages) };

    private static int[] Indexes(IEnumerable<ProbeStream> streams) => streams.Select(s => s.index).ToArray();

    [Fact]
    public void Keeps_chosen_languages_and_removes_the_rest()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "fre"), Sub(4, "spa"), Sub(5, "ger"));

        var plan = SubtitlePlanner.Plan(probe, Keep("eng, spa"), Video);

        Assert.Equal(new[] { 2, 4 }, Indexes(plan.Keep));
        Assert.Equal(new[] { 3, 5 }, Indexes(plan.Remove));
        Assert.True(plan.NeedsRemux);
    }

    [Fact]
    public void Language_match_ignores_case()
    {
        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "ENG")), Keep("eng"), Video);
        Assert.Empty(plan.Remove);
        Assert.False(plan.HasWork);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void Undefined_language_follows_the_KeepUndefined_option(bool keepUndefined, int expectedRemoved)
    {
        var settings = Keep("eng");
        settings.KeepUndefined = keepUndefined;

        var plan = SubtitlePlanner.Plan(Movie(Sub(2, null), Sub(3, "und")), settings, Video);

        Assert.Equal(expectedRemoved * 2, plan.Remove.Count);
    }

    [Fact]
    public void Forced_tracks_in_other_languages_are_kept_only_when_asked()
    {
        var probe = Movie(Sub(2, "eng"), Sub(3, "jpn", forced: true));

        Assert.Equal(new[] { 3 }, Indexes(SubtitlePlanner.Plan(probe, Keep("eng"), Video).Remove));

        var settings = Keep("eng");
        settings.KeepForcedAnyLanguage = true;
        Assert.Empty(SubtitlePlanner.Plan(probe, settings, Video).Remove);
    }

    [Fact]
    public void Hearing_impaired_tracks_can_be_removed_even_in_kept_languages()
    {
        var settings = Keep("eng");
        settings.RemoveHearingImpaired = true;
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng", sdh: true), Sub(4, "eng", title: "English SDH"));

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Equal(new[] { 2 }, Indexes(plan.Keep));
        Assert.Equal(new[] { 3, 4 }, Indexes(plan.Remove));
    }

    [Fact]
    public void Non_subtitle_streams_are_never_touched()
    {
        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "fre")), Keep("eng"), Video);
        Assert.Equal(new[] { 2 }, Indexes(plan.Remove));
        Assert.Empty(plan.Keep);
    }

    [Fact]
    public void Extracts_kept_text_subtitles_with_emby_sidecar_names()
    {
        var settings = Keep("eng");
        settings.ExtractTextSubtitles = true;
        var probe = Movie(Sub(2, "eng"), Sub(3, "eng", forced: true), Sub(4, "eng", codec: "hdmv_pgs_subtitle"), Sub(5, "eng", codec: "ass"));

        var plan = SubtitlePlanner.Plan(probe, settings, Video);

        Assert.Equal(new[]
        {
            @"D:\Movies\Heat (1995)\Heat (1995).eng.srt",
            @"D:\Movies\Heat (1995)\Heat (1995).eng.forced.srt",
            @"D:\Movies\Heat (1995)\Heat (1995).eng.ass",
        }.Select(p => p.Replace('\\', Path.DirectorySeparatorChar)), plan.Extract.Select(e => e.OutputPath.Replace('\\', Path.DirectorySeparatorChar)));
        Assert.True(plan.Extract[0].ConvertToSrt);
        Assert.False(plan.Extract[2].ConvertToSrt);
        Assert.Equal(4, plan.Keep.Count);
        Assert.False(plan.NeedsRemux);
    }

    [Fact]
    public void Picture_subtitles_are_never_extracted_or_removed_by_the_extract_option()
    {
        var settings = Keep("eng");
        settings.ExtractTextSubtitles = true;
        settings.RemoveExtractedFromVideo = true;

        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "eng", codec: "hdmv_pgs_subtitle"), Sub(3, "eng")), settings, Video);

        Assert.Equal(new[] { 2 }, Indexes(plan.Keep));
        Assert.Equal(new[] { 3 }, Indexes(plan.Remove));
        Assert.Single(plan.Extract);
    }

    [Fact]
    public void Two_tracks_with_the_same_sidecar_name_get_unique_files()
    {
        var settings = Keep("eng");
        settings.ExtractTextSubtitles = true;

        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "eng"), Sub(3, "eng")), settings, Video);

        Assert.EndsWith("Heat (1995).eng.srt", plan.Extract[0].OutputPath);
        Assert.EndsWith("Heat (1995).eng.track3.srt", plan.Extract[1].OutputPath);
    }

    [Fact]
    public void ParseLanguages_accepts_commas_semicolons_and_spaces()
    {
        var langs = PlannerSettings.ParseLanguages(" eng,SPA; fre  ger,,");
        Assert.Equal(new[] { "eng", "fre", "ger", "spa" }, langs.OrderBy(x => x));
    }

    [Fact]
    public void Reads_real_ffprobe_json()
    {
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_name": "hevc", "codec_type": "video", "disposition": { "default": 1, "forced": 0 } },
            { "index": 1, "codec_name": "truehd", "codec_type": "audio", "tags": { "language": "eng" } },
            { "index": 2, "codec_name": "hdmv_pgs_subtitle", "codec_type": "subtitle",
              "disposition": { "default": 0, "forced": 1, "hearing_impaired": 0 }, "tags": { "LANGUAGE": "eng", "title": "Forced" } },
            { "index": 3, "codec_name": "subrip", "codec_type": "subtitle",
              "disposition": { "default": 0, "forced": 0, "hearing_impaired": 1 }, "tags": { "language": "fre" } }
          ],
          "format": { "filename": "x.mkv", "format_name": "matroska,webm", "duration": "7265.123000" }
        }
        """;

        var probe = Parse(json);

        Assert.Equal(1, probe.Count("video"));
        Assert.Equal(2, probe.Count("subtitle"));
        Assert.Equal(7265.123, probe.DurationSeconds!.Value, 3);
        Assert.Equal("eng", probe.streams[2].Language);
        Assert.True(probe.streams[2].IsForced);
        Assert.True(probe.streams[3].IsHearingImpaired);
    }
}
