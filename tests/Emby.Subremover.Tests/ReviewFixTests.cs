using Emby.Subremover.Core;
using MediaBrowser.Model.Logging;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public sealed class ReviewFixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subremover-review-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Srt(int cues, int lastCueStartSeconds)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= cues; i++)
        {
            var start = TimeSpan.FromSeconds(lastCueStartSeconds * (double)i / cues);
            var end = start + TimeSpan.FromSeconds(1);
            sb.Append($"{i}\n{start:hh\\:mm\\:ss\\,fff} --> {end:hh\\:mm\\:ss\\,fff}\nLine {i}\n\n");
        }
        return sb.ToString();
    }

    private static ProbeStream Track(string frames = null, string duration = null)
    {
        var s = Sub(3, "eng");
        if (frames != null) s.tags["NUMBER_OF_FRAMES"] = frames;
        if (duration != null) s.tags["DURATION"] = duration;
        return s;
    }

    private string WriteFile(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ---- 1. extracted subtitles must be complete

    [Fact]
    public void Cut_off_srt_is_caught_by_subtitle_count()
    {
        var file = WriteFile("a.srt", Srt(9, 60));
        Assert.Equal("only 9 of 20 subtitles", OutputChecks.CheckSubtitleFile(file, Track(frames: "20"), isAss: false));
    }

    [Fact]
    public void Cut_off_srt_is_caught_by_end_time_like_the_backrooms_files()
    {
        var file = WriteFile("a.srt", Srt(961, 5141));
        var problem = OutputChecks.CheckSubtitleFile(file, Track(duration: "01:40:50.753000000"), isAss: false);
        Assert.StartsWith("ends at 1:25:4", problem);
        Assert.EndsWith("runs to 1:40:50", problem);
    }

    [Fact]
    public void Complete_srt_passes_and_files_without_statistics_only_need_content()
    {
        var file = WriteFile("a.srt", Srt(20, 600));
        Assert.Null(OutputChecks.CheckSubtitleFile(file, Track(frames: "20", duration: "00:10:01.000000000"), isAss: false));
        Assert.Null(OutputChecks.CheckSubtitleFile(file, Track(), isAss: false));
        Assert.Equal("no subtitles in the file", OutputChecks.CheckSubtitleFile(WriteFile("b.srt", ""), Track(), isAss: false));
    }

    [Fact]
    public void Ass_files_are_counted_by_dialogue_lines()
    {
        var ass = "[Events]\nFormat: Layer, Start, End, Style, Text\n" +
                  string.Concat(Enumerable.Range(1, 10).Select(i => $"Dialogue: 0,0:00:{i:00}.00,0:00:{i:00}.90,Default,,0,0,0,,Line {i}\n"));
        var file = WriteFile("a.ass", ass);
        Assert.Null(OutputChecks.CheckSubtitleFile(file, Track(frames: "10", duration: "00:00:10.900000000"), isAss: true));
        Assert.Equal("only 10 of 30 subtitles", OutputChecks.CheckSubtitleFile(file, Track(frames: "30"), isAss: true));
    }

    [Fact]
    public void Ocr_output_skips_the_count_check()
    {
        var file = WriteFile("a.srt", Srt(10, 60));
        Assert.Null(OutputChecks.CheckSubtitleFile(file, Track(frames: "20"), isAss: false, compareCount: false));
    }

    [Theory]
    [InlineData("libebml v1.4.5 + libmatroska v1.7.1", true)]
    [InlineData("Lavf59.27.100", false)]
    [InlineData(null, true)]
    public void Subtitle_counts_are_only_trusted_when_ffmpeg_did_not_write_the_file(string encoder, bool trusted)
    {
        var tags = encoder == null ? "" : $", \"tags\": {{ \"ENCODER\": \"{encoder}\" }}";
        var probe = Parse($"{{ \"streams\": [], \"format\": {{ \"duration\": \"60\"{tags} }} }}");
        Assert.Equal(trusted, probe.FrameCountsAreCurrent);
    }

    [Theory]
    [InlineData("[matroska,webm @ 0x1] File ended prematurely", true)]
    [InlineData("av_interleaved_write_frame(): I/O error", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Ffmpeg_truncation_messages_are_recognised(string stderr, bool truncated)
    {
        Assert.Equal(truncated, OutputChecks.TruncationMessage(stderr) != null);
    }

    // ---- 2. a shortened video must not pass

    [Fact]
    public void Two_hour_movie_missing_the_last_minute_fails_verification()
    {
        var before = Movie(Sub(2, "eng"), Sub(3, "fre"));
        before.format.duration = "7200";
        var cut = Movie(Sub(2, "eng"));
        cut.format.duration = "7140";
        var fine = Movie(Sub(2, "eng"));
        fine.format.duration = "7198.5";

        Assert.Contains("duration changed", RemuxVerifier.Verify(before, cut, 1, 1000, 990));
        Assert.Null(RemuxVerifier.Verify(before, fine, 1, 1000, 990));
    }

    // ---- 3. state saved safely, one task at a time

    [Fact]
    public void State_round_trips_and_is_replaced_without_temp_leftovers()
    {
        var store = new StateStore(_dir, new TestJsonSerializer());
        var state = new SubremoverState { InProgress = "/m/a.mkv" };
        state.MarkRestored("/m/b.mkv");
        store.Save(state);
        state.Backups.Add(new BackupEntry { OriginalPath = "/m/c.mkv", BackupPath = "/m/c.mkv.subremover.bak" });
        store.Save(state);

        var loaded = new StateStore(_dir, new TestJsonSerializer()).Load();

        Assert.Equal("/m/a.mkv", loaded.InProgress);
        Assert.True(loaded.IsRestored("/M/B.MKV"));
        Assert.Single(loaded.Backups);
        Assert.Equal(new[] { "Emby.Subremover.state.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Only_one_task_can_hold_the_gate()
    {
        Assert.True(TaskGate.TryEnter());
        try
        {
            Assert.False(TaskGate.TryEnter());
        }
        finally
        {
            TaskGate.Exit();
        }
        Assert.True(TaskGate.TryEnter());
        TaskGate.Exit();
    }

    // ---- 4. restored videos stay restored

    [Fact]
    public void Settings_change_rechecks_videos_but_keeps_restored_ones_alone()
    {
        var state = new SubremoverState { SettingsSignature = "old" };
        state.Processed["/m/a.mkv"] = new FileStamp();
        state.MarkRestored("/m/b.mkv");

        Assert.True(state.ApplySettingsSignature("new"));
        Assert.Empty(state.Processed);
        Assert.True(state.IsRestored("/m/b.mkv"));
        Assert.False(state.ApplySettingsSignature("new"));

        state.ForgetEverything();
        Assert.False(state.IsRestored("/m/b.mkv"));
    }

    // ---- 5. display-only data never reaches the settings file

    [Fact]
    public void Settings_page_copy_and_saves_never_write_display_data()
    {
        var store = new OptionsStore(_dir, "Subremover", new TestJsonSerializer(), new NullLogger());
        var forPage = store.GetCopy();
        forPage.LogText = "a very big log text";
        forPage.LanguageChoices.Add(new Emby.Web.GenericEdit.Common.EditorSelectOption { Value = "eng", Name = "English (eng)" });
        forPage.KeepLanguages = "eng,spa";

        Assert.Null(store.Get().LogText);

        store.Save(forPage);

        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("a very big log text", json);
        Assert.DoesNotContain("English (eng)", json);
        Assert.Equal("eng,spa", new OptionsStore(_dir, "Subremover", new TestJsonSerializer(), new NullLogger()).Get().KeepLanguages);
        Assert.Equal("a very big log text", forPage.LogText);
    }

    [Fact]
    public void Display_data_already_in_an_old_settings_file_is_dropped_on_load()
    {
        var old = new PluginOptions { LogText = "leaked log", KeepLanguages = "eng" };
        File.WriteAllText(Path.Combine(_dir, "Subremover.json"), new TestJsonSerializer().SerializeToString(old));

        var loaded = new OptionsStore(_dir, "Subremover", new TestJsonSerializer(), new NullLogger()).Get();

        Assert.Null(loaded.LogText);
        Assert.Equal("eng", loaded.KeepLanguages);
    }

    // ---- 6. leftovers from an interrupted run are cleaned up

    [Fact]
    public void Leftover_temp_files_for_the_interrupted_video_are_deleted()
    {
        var video = WriteFile("Movie (2020).mkv", "video");
        WriteFile("Movie (2020).mkv.subremover-tmp", "half a remux");
        WriteFile("Movie (2020).eng.srt.subremover-tmp", "half a subtitle");
        WriteFile("Movie (2020).mkv.subremover-restore", "half a restore");
        WriteFile("Movie (2020).eng.srt", "keep me");
        WriteFile("Movie (2020).mkv.subremover.bak", "backup, keep me");
        WriteFile("Other (2021).mkv.subremover-tmp", "not this video");

        var deleted = Leftovers.Clean(video).Select(Path.GetFileName).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Movie (2020).eng.srt.subremover-tmp", "Movie (2020).mkv.subremover-restore", "Movie (2020).mkv.subremover-tmp" }, deleted);
        Assert.True(File.Exists(video));
        Assert.True(File.Exists(Path.Combine(_dir, "Movie (2020).eng.srt")));
        Assert.True(File.Exists(Path.Combine(_dir, "Movie (2020).mkv.subremover.bak")));
        Assert.True(File.Exists(Path.Combine(_dir, "Other (2021).mkv.subremover-tmp")));
    }
}
