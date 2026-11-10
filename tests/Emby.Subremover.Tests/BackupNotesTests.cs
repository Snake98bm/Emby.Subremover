using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class BackupNotesTests
{
    private const string Video = @"\\nas\Emby\Movies\My Movie (2020)\My Movie (2020).mkv";

    [Fact]
    public void A_busy_change_takes_two_lines()
    {
        var plan = new SubtitlePlan();
        plan.Remove.AddRange(new[]
        {
            Sub(3, "fre"), Sub(4, "spa"), Sub(5, "spa", sdh: true), Sub(6, "spa"), Sub(7, "ger", codec: "hdmv_pgs_subtitle"),
            Sub(8, "eng", title: "Commentary")
        });
        plan.NewDefault = Sub(2, "eng");
        var made = new[] { new MadeFile { Path = @"\\nas\Emby\Movies\My Movie (2020)\My Movie (2020).pgs.eng.srt", Ocr = true } };

        var lines = BackupNotes.Describe(Video, plan, made, OcrModels.Both);

        Assert.Equal(new[]
        {
            "Removed 6 subtitles: French, Spanish (2), Spanish SDH, German, English commentary",
            "Made .pgs.eng.srt (OCR, both models) · default now English"
        }, lines);
    }

    [Fact]
    public void A_small_change_fits_on_one_line()
    {
        var plan = new SubtitlePlan();
        plan.Remove.AddRange(new[] { Sub(3, "eng", sdh: true), Sub(4, "ara") });
        var made = new[] { new MadeFile { Path = @"\\nas\Emby\Movies\My Movie (2020)\My Movie (2020).eng.srt" } };

        Assert.Equal(new[] { "Removed 2 subtitles: English SDH, Arabic · extracted .eng.srt" },
            BackupNotes.Describe(Video, plan, made, OcrModels.Fast));
    }

    [Fact]
    public void A_change_with_nothing_removed_still_says_what_happened()
    {
        var plan = new SubtitlePlan { NewDefault = Sub(2, "eng") };
        Assert.Equal(new[] { "Default now English" }, BackupNotes.Describe(Video, plan, null, OcrModels.Both));
    }

    [Fact]
    public void Handing_the_default_to_a_downloaded_file_says_which_track_and_which_file()
    {
        var plan = new SubtitlePlan();
        plan.Keep.Add(Sub(2, "eng", sdh: true, isDefault: true));
        plan.ClearDefault.Add(plan.Keep[0]);
        plan.LeftToExistingFiles["eng"] = "My Movie (2020).eng.sdh.srt";

        Assert.Equal(new[] { "Turned off the default on the built-in English SDH subtitle, so Emby shows .eng.sdh.srt instead" },
            BackupNotes.Describe(Video, plan, null, OcrModels.Both));
    }

    [Fact]
    public void A_long_list_is_cut_short()
    {
        var plan = new SubtitlePlan();
        plan.Remove.AddRange(new[] { "fre", "spa", "ger", "ita", "por", "dut", "swe", "nor" }.Select((l, i) => Sub(3 + i, l)));
        var line = Assert.Single(BackupNotes.Describe(Video, plan, null, OcrModels.Both));
        Assert.EndsWith("and 2 more", line);
    }

    [Fact]
    public void Older_backups_say_their_changes_werent_recorded_and_test_runs_keep_the_notes()
    {
        var state = new SubremoverState();
        state.Backups.Add(new BackupEntry { OriginalPath = Video, BackupPath = Video + ".subremover.bak", Changes = new() { "Removed 1 subtitle: French" } });
        state.Backups.Add(new BackupEntry { OriginalPath = Video + "2", BackupPath = Video + "2.subremover.bak" });

        var preview = state.PreviewFor("sig", recheckEverything: false);
        Assert.Equal(new[] { "Removed 1 subtitle: French" }, preview.Backups[0].Changes);
        Assert.Null(preview.Backups[1].Changes);
    }
}
