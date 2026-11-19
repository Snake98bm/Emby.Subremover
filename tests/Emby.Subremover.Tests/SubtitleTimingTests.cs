using System.Text;
using Emby.Subremover.Core;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Subremover.Tests;

public class SubtitleTimingTests
{
    // ---- a made-up film: cues at random times, "speech" under each from its start until 0.6 s before its end

    private static List<CueTime> RandomCues(int count, int seed, long startMs = 4000)
    {
        var rnd = new Random(seed);
        var cues = new List<CueTime>();
        var t = startMs;
        for (var i = 0; i < count; i++)
        {
            var length = rnd.Next(1200, 4500);
            cues.Add(new CueTime(t, t + length));
            t += length + rnd.Next(400, 7000);
        }
        return cues;
    }

    private static string ToSrt(IEnumerable<CueTime> cues) =>
        string.Concat(cues.Select((c, i) => $"{i + 1}\r\n{SrtTimes.Format(c.StartMs)} --> {SrtTimes.Format(c.EndMs)}\r\nLine number {i + 1}.\r\n\r\n"));

    private static SpeechTrack SpeechFor(IList<CueTime> cues, int seed = 1)
    {
        var rnd = new Random(seed);
        var frames = (int)(cues[^1].EndMs / SpeechTrack.FrameMs) + 500;
        var level = new float[frames];
        for (var i = 0; i < frames; i++) level[i] = 2f + (float)rnd.NextDouble() * 0.5f;
        foreach (var c in cues)
            for (var f = c.StartMs / SpeechTrack.FrameMs; f < (c.EndMs - 600) / SpeechTrack.FrameMs; f++)
                level[f] = 4.5f + (float)rnd.NextDouble();
        return SpeechTrack.FromEnergies(level);
    }

    private static List<CueTime> Moved(IEnumerable<CueTime> cues, long ms) => cues.Select(c => new CueTime(c.StartMs + ms, c.EndMs + ms)).ToList();

    [Fact]
    public void A_file_that_matches_the_speech_is_in_sync()
    {
        var cues = RandomCues(150, 3);
        var r = SubtitleSync.Measure(SpeechFor(cues), cues);
        Assert.Equal(TimingVerdict.InSync, r.Verdict);
        Assert.InRange(r.BestOffsetMs, -40, 40);
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(-800)]
    [InlineData(-45000)]
    [InlineData(90000)]
    public void A_file_off_by_the_same_amount_throughout_is_moved_back(long late)
    {
        var cues = RandomCues(150, 4, startMs: 100000);
        var r = SubtitleSync.Measure(SpeechFor(cues), Moved(cues, late));
        Assert.Equal(TimingVerdict.Shift, r.Verdict);
        Assert.InRange(r.OffsetMs, -late - 40, -late + 40);
        Assert.True(r.PartOffsetsMs.Max() - r.PartOffsetsMs.Min() <= 40);
    }

    [Fact]
    public void A_small_offset_is_left_alone()
    {
        var cues = RandomCues(150, 5);
        var r = SubtitleSync.Measure(SpeechFor(cues), Moved(cues, 160));
        Assert.Equal(TimingVerdict.InSync, r.Verdict);
        Assert.Equal(0, r.OffsetMs);
    }

    [Fact]
    public void A_file_made_for_another_frame_rate_is_never_stretched()
    {
        var cues = RandomCues(300, 6);
        var stretched = cues.Select(c => new CueTime((long)(c.StartMs * 25 / 23.976), (long)(c.EndMs * 25 / 23.976))).ToList();
        var r = SubtitleSync.Measure(SpeechFor(cues), stretched);
        Assert.NotEqual(TimingVerdict.Shift, r.Verdict);
        Assert.NotEqual(TimingVerdict.InSync, r.Verdict);
    }

    [Fact]
    public void A_file_that_jumps_part_way_is_left_alone()
    {
        var cues = RandomCues(150, 7);
        var jump = cues.Select((c, i) => i < 100 ? c : new CueTime(c.StartMs + 4000, c.EndMs + 4000)).ToList();
        var r = SubtitleSync.Measure(SpeechFor(cues), jump);
        Assert.Equal(TimingVerdict.Drifts, r.Verdict);
    }

    [Fact]
    public void A_wobble_under_half_a_second_counts_as_in_sync_not_drifting()
    {
        // Two thirds in sync, the last third 0.44 s late: more than the 0.4 s spread, but nobody can see it.
        var cues = RandomCues(150, 7);
        var wobble = cues.Select((c, i) => i < 100 ? c : new CueTime(c.StartMs + 440, c.EndMs + 440)).ToList();
        var r = SubtitleSync.Measure(SpeechFor(cues), wobble);
        Assert.Equal(TimingVerdict.InSync, r.Verdict);
    }

    [Fact]
    public void A_near_sync_copy_is_not_deleted_next_to_an_in_sync_one()
    {
        var cues = RandomCues(150, 7);
        var speech = SpeechFor(cues);
        var wobble = cues.Select((c, i) => i < 100 ? c : new CueTime(c.StartMs + 440, c.EndMs + 440)).ToList();
        var subs = new List<MeasuredSubtitle>
        {
            new() { Path = "My Movie (2020).en.srt", Language = "eng", Cues = 150, Result = SubtitleSync.Measure(speech, cues) },
            new() { Path = "My Movie (2020).eng.srt", Language = "eng", Cues = 150, Result = SubtitleSync.Measure(speech, wobble) }
        };
        SubtitleTimingFiles.Decide(subs, deleteOffDuplicates: true, deleteOtherVersions: true);
        Assert.All(subs, s => Assert.Equal(TimingAction.Keep, s.Action));
    }

    [Fact]
    public void Cues_that_have_nothing_to_do_with_the_speech_are_unsure()
    {
        var cues = RandomCues(150, 8);
        var r = SubtitleSync.Measure(SpeechFor(cues), RandomCues(150, 99));
        Assert.Equal(TimingVerdict.Unsure, r.Verdict);
    }

    [Fact]
    public void Too_few_cues_are_not_measured()
    {
        var cues = RandomCues(10, 9);
        Assert.Equal(TimingVerdict.Unsure, SubtitleSync.Measure(SpeechFor(cues), cues).Verdict);
    }

    // ---- reading and moving .srt times

    [Fact]
    public void Moving_changes_only_the_times()
    {
        var srt = "1\r\n00:00:01,000 --> 00:00:02,500 X1:10\r\n<i>Hello</i> 00:00:09,000\r\n\r\n2\r\n00:01:59,900 --> 00:02:00,100\r\nBye\r\n";
        var moved = SrtTimes.Shift(srt, 2150);
        Assert.Equal("1\r\n00:00:03,150 --> 00:00:04,650 X1:10\r\n<i>Hello</i> 00:00:09,000\r\n\r\n2\r\n00:02:02,050 --> 00:02:02,250\r\nBye\r\n", moved);
    }

    [Fact]
    public void Moving_before_the_start_stops_at_zero()
    {
        Assert.Equal("1\n00:00:00,000 --> 00:00:00,500\nHi\n", SrtTimes.Shift("1\n00:00:01,000 --> 00:00:02,500\nHi\n", -2000));
    }

    [Fact]
    public void Times_with_dots_and_short_fractions_are_read()
    {
        var cues = SrtTimes.Read("1\n0:00:01.5 --> 0:00:02.25\nHi\n\n2\n00:00:03,000 --> 00:00:02,000\nbackwards\n");
        var c = Assert.Single(cues);
        Assert.Equal(1500, c.StartMs);
        Assert.Equal(2250, c.EndMs);
    }

    // ---- which file is which, and what happens to it

    [Theory]
    [InlineData("My Movie (2020).en.srt", "eng", null)]
    [InlineData("My Movie (2020).eng.default.srt", "eng", null)]
    [InlineData("My Movie (2020).srt", "", null)]
    [InlineData("My Movie (2020).en.sdh.srt", "eng", "SDH")]
    [InlineData("My Movie (2020).en.hi.srt", "eng", "SDH")]
    [InlineData("My Movie (2020).en.forced.srt", "eng", "forced")]
    [InlineData("My Movie (2020).English.Commentary.srt", "eng", "commentary")]
    [InlineData("My Movie (2020).en.descriptive.srt", "eng", "audio description")]
    [InlineData("My Movie (2020).fr.srt", "fre", null)]
    public void Files_are_told_apart_by_name(string file, string language, string special)
    {
        var sub = SubtitleTimingFiles.Describe(Path.Combine("x", file), Path.Combine("x", "My Movie (2020).mkv"), "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
        Assert.Equal(language, sub.Language);
        Assert.Equal(special, sub.Special);
    }

    private static MeasuredSubtitle Sub(string name, string language, TimingVerdict verdict, string special = null, double confidence = 20) => new()
    {
        Path = name, Language = language, Special = special, Cues = 500,
        Result = new TimingResult { Verdict = verdict, Confidence = confidence, OffsetMs = verdict == TimingVerdict.Shift ? 2000 : 0 }
    };

    [Fact]
    public void An_off_copy_goes_when_the_same_language_has_one_in_sync()
    {
        var good = Sub("a.en.srt", "eng", TimingVerdict.InSync);
        var shifted = Sub("a.eng.srt", "eng", TimingVerdict.Shift);
        var drifting = Sub("a.english.srt", "eng", TimingVerdict.Drifts);
        var unsure = Sub("a.en.2.srt", "eng", TimingVerdict.Unsure);
        var sdh = Sub("a.en.sdh.srt", "eng", TimingVerdict.Shift, "SDH");
        var commentary = Sub("a.en.commentary.srt", "eng", TimingVerdict.Unsure, "commentary");
        var french = Sub("a.fr.srt", "fre", TimingVerdict.Shift);
        var all = new List<MeasuredSubtitle> { good, shifted, drifting, unsure, sdh, commentary, french };

        SubtitleTimingFiles.Decide(all, deleteOffDuplicates: true);

        Assert.Equal(TimingAction.Keep, good.Action);
        Assert.Equal(TimingAction.Delete, shifted.Action);
        Assert.Equal("a.en.srt", shifted.KeptInstead);
        Assert.Equal(TimingAction.Delete, drifting.Action);
        Assert.Equal(TimingAction.Delete, unsure.Action);
        Assert.Equal(TimingAction.Shift, sdh.Action);
        Assert.Equal(TimingAction.LeaveAlone, commentary.Action);
        Assert.Equal(TimingAction.Shift, french.Action);
    }

    [Fact]
    public void Without_an_in_sync_copy_nothing_is_deleted()
    {
        var a = Sub("a.en.srt", "eng", TimingVerdict.Shift);
        var b = Sub("a.eng.srt", "eng", TimingVerdict.Drifts);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { a, b }, deleteOffDuplicates: true);
        Assert.Equal(TimingAction.Shift, a.Action);
        Assert.Equal(TimingAction.LeaveAlone, b.Action);
    }

    [Fact]
    public void With_deleting_off_the_off_copy_is_moved_instead()
    {
        var a = Sub("a.en.srt", "eng", TimingVerdict.InSync);
        var b = Sub("a.eng.srt", "eng", TimingVerdict.Shift);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { a, b }, deleteOffDuplicates: false);
        Assert.Equal(TimingAction.Keep, a.Action);
        Assert.Equal(TimingAction.Shift, b.Action);
    }

    private static MeasuredSubtitle Drifting(string name, long start, long middle, long end, double confidence = 15)
    {
        var s = Sub(name, "eng", TimingVerdict.Drifts, confidence: confidence);
        s.Result.PartOffsetsMs = new[] { start, middle, end };
        return s;
    }

    [Fact]
    public void The_only_file_of_a_language_is_never_deleted()
    {
        // Made for another cut, or drifting a second by the end: logged, but it's all there is in English.
        var onlyOne = Drifting("a.en.srt", 160, -380, -940);
        var unsure = Sub("a.fr.srt", "fre", TimingVerdict.Unsure);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { onlyOne, unsure }, deleteOffDuplicates: true, deleteOtherVersions: true);
        Assert.Equal(TimingAction.LeaveAlone, onlyOne.Action);
        Assert.Equal(TimingAction.LeaveAlone, unsure.Action);
    }

    [Fact]
    public void With_none_in_sync_the_best_copy_stays_and_worse_ones_go()
    {
        // One can be moved: it's the best; the drifting and unclear ones go.
        var movable = Sub("a.en.srt", "eng", TimingVerdict.Shift);
        var drifting = Drifting("a.eng.srt", 80, 80, 14080);
        var unsure = Sub("a.english.srt", "eng", TimingVerdict.Unsure);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { drifting, unsure, movable }, deleteOffDuplicates: true, deleteOtherVersions: true);
        Assert.Equal(TimingAction.Shift, movable.Action);
        Assert.Equal(TimingAction.Delete, drifting.Action);
        Assert.Equal(TimingAction.Delete, unsure.Action);
        Assert.Equal("a.en.srt", drifting.KeptInstead);
        Assert.False(drifting.KeptInSync);

        // Two that both drift: the one closest to one offset stays.
        var small = Drifting("b.en.srt", 0, -120, -560);
        var big = Drifting("b.eng.srt", -64140, -84140, -84140);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { big, small }, deleteOffDuplicates: true, deleteOtherVersions: true);
        Assert.Equal(TimingAction.LeaveAlone, small.Action);
        Assert.Equal(TimingAction.Delete, big.Action);
        Assert.Equal("b.en.srt", big.KeptInstead);
    }

    [Fact]
    public void Without_the_setting_worse_copies_with_none_in_sync_stay()
    {
        var movable = Sub("a.en.srt", "eng", TimingVerdict.Shift);
        var drifting = Drifting("a.eng.srt", 80, 80, 14080);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { movable, drifting }, deleteOffDuplicates: true, deleteOtherVersions: false);
        Assert.Equal(TimingAction.Shift, movable.Action);
        Assert.Equal(TimingAction.LeaveAlone, drifting.Action);
    }

    [Fact]
    public void Two_movable_copies_are_both_moved()
    {
        var a = Sub("a.en.srt", "eng", TimingVerdict.Shift);
        var b = Sub("a.eng.srt", "eng", TimingVerdict.Shift);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { a, b }, deleteOffDuplicates: true, deleteOtherVersions: true);
        Assert.Equal(TimingAction.Shift, a.Action);
        Assert.Equal(TimingAction.Shift, b.Action);
    }

    [Fact]
    public void Sdh_forced_and_commentary_files_are_never_deleted_as_worse_copies()
    {
        var regular = Sub("a.en.srt", "eng", TimingVerdict.InSync);
        var forced = Sub("a.en.forced.srt", "eng", TimingVerdict.Drifts, special: "forced");
        var sdh = Drifting("a.en.sdh.srt", 0, 900, 3000);
        sdh.Special = "SDH";
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { regular, forced, sdh }, deleteOffDuplicates: true, deleteOtherVersions: true);
        Assert.Equal(TimingAction.LeaveAlone, forced.Action);
        Assert.Equal(TimingAction.LeaveAlone, sdh.Action);
    }

    [Fact]
    public void Two_in_sync_copies_both_stay()
    {
        var a = Sub("a.en.srt", "eng", TimingVerdict.InSync);
        var b = Sub("a.eng.srt", "eng", TimingVerdict.InSync);
        SubtitleTimingFiles.Decide(new List<MeasuredSubtitle> { a, b }, deleteOffDuplicates: true);
        Assert.All(new[] { a, b }, s => Assert.Equal(TimingAction.Keep, s.Action));
    }

    [Fact]
    public void A_deleted_file_comes_back_from_its_backup()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-timing-").FullName;
        try
        {
            var srt = Path.Combine(dir, "My Movie (2020).en.srt");
            File.WriteAllText(srt, "hello");
            var removed = BackupManager.Remove(srt, BackupMode.NextToOriginal, null);
            Assert.True(removed.Success);
            Assert.False(File.Exists(srt));
            BackupManager.Restore(removed.NewBackup);
            Assert.Equal("hello", File.ReadAllText(srt));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- the whole thing, with Emby's ffmpeg reading a real 5.1 audio track

    private static string FindFfmpegFolder()
    {
        var exe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var fromEnv = Environment.GetEnvironmentVariable("SUBREMOVER_FFMPEG_DIR");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(Path.Combine(fromEnv, exe))) return fromEnv;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg");
            if (File.Exists(Path.Combine(candidate, exe))) return candidate;
        }
        return null;
    }

    // 6-channel 8 kHz WAV: "speech" (noise with a syllable rhythm) in the centre under each cue, loud noise left and right.
    private static void WriteFilm(string path, IList<CueTime> cues)
    {
        const int rate = 8000, channels = 6;
        var samples = (int)((cues[^1].EndMs + 5000) * rate / 1000);
        var rnd = new Random(11);
        var speaking = new bool[samples];
        foreach (var c in cues)
            for (var i = (int)(c.StartMs * rate / 1000); i < (int)((c.EndMs - 600) * rate / 1000) && i < samples; i++) speaking[i] = true;
        using var w = new BinaryWriter(File.Create(path));
        var dataBytes = samples * channels * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
        for (var i = 0; i < samples; i++)
        {
            var syllable = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * i / rate);
            var centre = speaking[i] ? (rnd.NextDouble() - 0.5) * 12000 * syllable : (rnd.NextDouble() - 0.5) * 200;
            var side = (rnd.NextDouble() - 0.5) * 9000;
            for (var ch = 0; ch < channels; ch++)
                w.Write((short)(ch == 2 ? centre : ch < 2 ? side : (rnd.NextDouble() - 0.5) * 2000));
        }
    }

    [Fact]
    public async Task A_video_with_only_an_extracted_copy_isnt_read()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-timing-").FullName;
        try
        {
            var video = Path.Combine(dir, "My Movie (2020).mkv");
            File.WriteAllText(video, "not a real video: reading it would fail");
            var copy = Path.Combine(dir, "My Movie (2020).eng.srt");
            File.WriteAllText(copy, ToSrt(RandomCues(80, 5)));
            var state = new SubremoverState();
            state.MadeFileSet().Add(copy);
            // No ffmpeg at all: if the audio were read, it would fail and count as Failed.
            var runner = new FfmpegRunner(() => Path.Combine(dir, "no-ffmpeg"), () => Path.Combine(dir, "no-ffprobe"));

            var files = new SubtitleTimingFiles(runner, false, true, false, BackupMode.NextToOriginal, null, new NullLogger()) { SkipExtracted = true };
            files.Begin(state);
            await files.Check(video, state, false, TestContext.Current.CancellationToken);

            Assert.Equal(0, files.VideosChecked);
            Assert.Equal(0, files.Failed);
            Assert.Equal(1, files.SkippedCopies);
            Assert.False(files.NeedsCheck(copy, state));   // noted, so later runs skip it too

            // Off: the copy is measured like any file (here that means reading the video, which fails).
            var measuring = new SubtitleTimingFiles(runner, false, true, false, BackupMode.NextToOriginal, null, new NullLogger());
            try { await measuring.Check(video, new SubremoverState(), false, TestContext.Current.CancellationToken); }
            catch (System.ComponentModel.Win32Exception) { }   // no ffmpeg here: starting it fails, as expected
            Assert.Equal(1, measuring.VideosChecked);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_video_with_two_english_files_keeps_the_one_in_sync()
    {
        var folder = FindFfmpegFolder();
        if (folder == null) Assert.Skip("Emby's ffmpeg not found in tools/ffmpeg (or SUBREMOVER_FFMPEG_DIR)");
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var runner = new FfmpegRunner(() => Path.Combine(folder, "ffmpeg" + exe), () => Path.Combine(folder, "ffprobe" + exe));
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("subremover-timing-").FullName;
        try
        {
            var cues = RandomCues(80, 21);
            var wav = Path.Combine(dir, "film.wav");
            WriteFilm(wav, cues);
            var video = Path.Combine(dir, "My Movie (2020).mkv");
            var made = await runner.RunFfmpeg(new List<string>
            {
                "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x48:r=5", "-i", wav,
                "-shortest", "-c:v", "mpeg4", "-c:a", "ac3", "-ar", "48000", video
            }, ct);
            Assert.True(made.Success, made.StdErr);
            File.Delete(wav);

            var good = Path.Combine(dir, "My Movie (2020).en.srt");
            var off = Path.Combine(dir, "My Movie (2020).eng.srt");
            var sdh = Path.Combine(dir, "My Movie (2020).en.sdh.srt");
            File.WriteAllText(good, ToSrt(cues));
            File.WriteAllText(off, ToSrt(Moved(cues, 3200)));
            File.WriteAllText(sdh, ToSrt(Moved(cues, -1500)));

            var state = new SubremoverState();
            var files = new SubtitleTimingFiles(runner, false, true, false, BackupMode.NextToOriginal, null, new NullLogger());
            files.Begin(state);
            await files.Check(video, state, false, ct);

            Assert.Equal(ToSrt(cues), File.ReadAllText(good));
            Assert.False(File.Exists(off));
            Assert.Equal(ToSrt(Moved(cues, 3200)), File.ReadAllText(off + BackupPaths.SideBySideSuffix));
            var fixedCues = SrtTimes.Read(File.ReadAllText(sdh));
            Assert.All(fixedCues.Zip(cues), p => Assert.InRange(p.First.StartMs - p.Second.StartMs, -60, 60));
            Assert.Equal(2, state.Backups.Count);
            Assert.Contains(state.Backups, b => b.OriginalPath == off && b.Changes.Single().StartsWith("Subtitle file: deleted, out of sync"));
            Assert.Contains(state.Backups, b => b.OriginalPath == sdh && b.Changes.Single().StartsWith("Subtitle file: moved every cue +1.50 s"));
            Assert.Equal(1, files.Shifted);
            Assert.Equal(1, files.Deleted);

            // Checked: a run doesn't read the audio again until a file changes.
            Assert.False(files.NeedsCheck(good, state));
            Assert.False(files.NeedsCheck(sdh, state));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SameNameVideosTests
{
    [Fact]
    public void Videos_with_the_same_name_in_one_folder_are_found()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-same-").FullName;
        try
        {
            foreach (var f in new[] { "My Movie (2020).mkv", "My Movie (2020).mp4", "My Movie (2020).en.srt", "My Movie (2020).nfo", "My Movie (2020) - Extended.mkv" })
                File.WriteAllText(Path.Combine(dir, f), "");
            var mkv = Path.Combine(dir, "My Movie (2020).mkv");
            Assert.Equal(new[] { Path.Combine(dir, "My Movie (2020).mp4") }, SubtitleTimingFiles.SameNameVideos(mkv));
            Assert.Empty(SubtitleTimingFiles.SameNameVideos(Path.Combine(dir, "My Movie (2020) - Extended.mkv")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
