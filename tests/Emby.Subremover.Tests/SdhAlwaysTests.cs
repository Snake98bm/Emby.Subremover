using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

// Forum request (1.0.6): "Remove SDH" kept the only full English subtitle of Avatar (2009) because it was SDH.
public class SdhAlwaysTests
{
    private const string Video = "/mnt/Filme/Movies/Avatar (2009)/Avatar (2009).mkv";

    private static int[] Indexes(IEnumerable<ProbeStream> streams) => streams.Select(s => s.index).ToArray();

    private static PlannerSettings Settings(bool always) => new()
    {
        KeepLanguages = PlannerSettings.ParseLanguages("eng,ger"),
        KeepForcedAnyLanguage = true,
        RemoveHearingImpaired = true,
        RemoveHearingImpairedAlways = always
    };

    // That video's tracks: German forced and full PGS, English forced PGS and full SDH PGS.
    private static ProbeResult Avatar() => Movie(
        Sub(4, "ger", codec: "hdmv_pgs_subtitle", forced: true, title: "Forced (PGS)"),
        Sub(5, "ger", codec: "hdmv_pgs_subtitle", title: "Full (PGS)"),
        Sub(7, "eng", codec: "hdmv_pgs_subtitle", forced: true, title: "Forced (PGS)"),
        Sub(8, "eng", codec: "hdmv_pgs_subtitle", sdh: true, title: "Full SDH (PGS)"));

    [Fact]
    public void Off_the_only_full_english_track_stays_even_though_it_is_sdh()
    {
        var plan = SubtitlePlanner.Plan(Avatar(), Settings(always: false), Video);
        Assert.Equal(new[] { 4, 5, 7, 8 }, Indexes(plan.Keep));
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void On_sdh_goes_even_when_it_is_the_only_full_track_and_forced_tracks_stay()
    {
        var settings = Settings(always: true);
        settings.OcrPictureSubtitles = true;

        var plan = SubtitlePlanner.Plan(Avatar(), settings, Video);

        Assert.Equal(new[] { 4, 5, 7 }, Indexes(plan.Keep));
        Assert.Equal(new[] { 8 }, Indexes(plan.Remove));
        Assert.DoesNotContain(plan.Ocr, j => j.Stream.index == 8);
    }

    [Fact]
    public void On_without_remove_sdh_does_nothing()
    {
        var settings = Settings(always: true);
        settings.RemoveHearingImpaired = false;
        Assert.Empty(SubtitlePlanner.Plan(Avatar(), settings, Video).Remove);
        Assert.DoesNotContain("SdhAlways", settings.Signature());
    }

    [Fact]
    public void Only_turning_it_on_changes_the_signature_so_upgrading_rechecks_nothing()
    {
        var before = new PlannerSettings { RemoveHearingImpaired = true }.Signature();
        Assert.Equal(before, new PlannerSettings { RemoveHearingImpaired = true, RemoveHearingImpairedAlways = false }.Signature());
        Assert.NotEqual(before, new PlannerSettings { RemoveHearingImpaired = true, RemoveHearingImpairedAlways = true }.Signature());
    }

    [Fact]
    public void The_option_sits_under_remove_sdh_and_shows_only_with_it_on()
    {
        Assert.False(new PluginOptions().RemoveHearingImpairedAlways);
        var tab = SettingsLayout.Tabs.Single(t => t.Id == SettingsTabId.Subtitles).Entries.Select(e => e.Item2).ToList();
        Assert.Equal(tab.IndexOf(nameof(PluginOptions.RemoveHearingImpaired)) + 1, tab.IndexOf(nameof(PluginOptions.RemoveHearingImpairedAlways)));
        Assert.Equal(nameof(PluginOptions.RemoveHearingImpaired), SettingsLayout.Describe(nameof(PluginOptions.RemoveHearingImpairedAlways)).VisibleIf.Field);
        Assert.True(MediaProcessor.SettingsFrom(new PluginOptions { RemoveHearingImpaired = true, RemoveHearingImpairedAlways = true }).RemoveHearingImpairedAlways);
    }
}
