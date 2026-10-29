using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

// Runs real ffmpeg on a tiny generated video. Uses Emby's ffmpeg copied into tools/ffmpeg (or the folder in
// the SUBREMOVER_FFMPEG_DIR environment variable); skipped when it isn't there.
public sealed class FfmpegIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subremover-ffmpeg-").FullName;
    private readonly FfmpegRunner _runner;

    public FfmpegIntegrationTests()
    {
        var folder = FindFfmpegFolder();
        if (folder == null) return;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        _runner = new FfmpegRunner(() => Path.Combine(folder, "ffmpeg" + exe), () => Path.Combine(folder, "ffprobe" + exe));
    }

    public void Dispose() => Directory.Delete(_dir, true);

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

    private void RequireFfmpeg()
    {
        if (_runner == null) Assert.Skip("Emby's ffmpeg not found in tools/ffmpeg (or SUBREMOVER_FFMPEG_DIR)");
    }

    private async Task<ProbeResult> Probe(string path)
    {
        var result = await _runner.RunFfprobe(FfmpegArgs.Probe(path), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.StdErr);
        return TestData.Parse(result.StdOut);
    }

    private static string Srt(string text) => $"1\n00:00:00,500 --> 00:00:02,000\n{text}\n\n";

    // 3-second video + audio with English, French and forced English text subtitles, plus a fourth "und" track.
    private async Task<string> MakeSampleVideo(string name)
    {
        var subs = new[] { ("eng", "Hello"), ("fre", "Bonjour"), ("eng", "FORCED"), ("", "unknown") };
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=3:size=160x120:rate=10",
            "-f", "lavfi", "-i", "sine=duration=3"
        };
        for (var i = 0; i < subs.Length; i++)
        {
            var srt = Path.Combine(_dir, $"sub{i}.srt");
            File.WriteAllText(srt, Srt(subs[i].Item2));
            args.AddRange(new[] { "-i", srt });
        }
        args.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
        for (var i = 0; i < subs.Length; i++) args.AddRange(new[] { "-map", $"{i + 2}:s" });
        args.AddRange(new[] { "-c:v", "mpeg4", "-c:a", "aac", "-c:s", "srt" });
        for (var i = 0; i < subs.Length; i++)
        {
            if (subs[i].Item1 != "") args.AddRange(new[] { $"-metadata:s:s:{i}", "language=" + subs[i].Item1 });
        }
        args.AddRange(new[] { "-disposition:s:2", "forced" });

        var output = Path.Combine(_dir, name);
        args.Add(output);

        var result = await _runner.RunFfmpeg(args, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.StdErr);
        return output;
    }

    [Fact]
    public async Task Removes_unwanted_subtitles_and_the_result_passes_verification()
    {
        RequireFfmpeg();
        var video = await MakeSampleVideo("Sample Movie (2020).mkv");
        var before = await Probe(video);
        Assert.Equal(4, before.Count("subtitle"));

        var plan = SubtitlePlanner.Plan(before, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), KeepUndefined = false }, video);
        Assert.Equal(2, plan.Remove.Count);

        var temp = video + ".subremover-tmp";
        var remux = await _runner.RunFfmpeg(FfmpegArgs.Remux(video, temp, "matroska", plan.Remove.Select(s => s.index)), TestContext.Current.CancellationToken);
        Assert.True(remux.Success, remux.StdErr);

        var after = await Probe(temp);
        Assert.Null(RemuxVerifier.Verify(before, after, plan.Remove.Count, new FileInfo(video).Length, new FileInfo(temp).Length));

        var subs = after.streams.Where(s => s.IsSubtitle).ToList();
        Assert.All(subs, s => Assert.Equal("eng", s.Language));
        Assert.Contains(subs, s => s.IsForced);

        var swap = BackupManager.Replace(video, temp, BackupMode.NextToOriginal, null);
        Assert.True(swap.Success, swap.Message);
        Assert.Equal(2, (await Probe(video)).Count("subtitle"));
        Assert.Equal(4, (await Probe(video + BackupPaths.SideBySideSuffix)).Count("subtitle"));
    }

    [Fact]
    public async Task Moves_the_default_flag_from_sdh_to_the_regular_track_and_keeps_the_other_flags()
    {
        RequireFfmpeg();
        var video = Path.Combine(_dir, "Default Test.mkv");
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=3:size=160x120:rate=10"
        };
        for (var i = 0; i < 3; i++)
        {
            var srt = Path.Combine(_dir, $"default{i}.srt");
            File.WriteAllText(srt, Srt("Line " + i));
            args.AddRange(new[] { "-i", srt });
        }
        args.AddRange(new[]
        {
            "-map", "0:v", "-map", "1:s", "-map", "2:s", "-map", "3:s", "-c:v", "mpeg4", "-c:s", "srt",
            "-metadata:s:s:0", "language=fre", "-metadata:s:s:1", "language=eng", "-metadata:s:s:2", "language=eng",
            "-disposition:s:0", "0", "-disposition:s:1", "default+hearing_impaired", "-disposition:s:2", "0", video
        });
        var made = await _runner.RunFfmpeg(args, TestContext.Current.CancellationToken);
        Assert.True(made.Success, made.StdErr);

        var before = await Probe(video);
        var plan = SubtitlePlanner.Plan(before, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), MakeRegularDefault = true }, video);
        Assert.Equal(3, plan.NewDefault.index);

        var temp = video + ".subremover-tmp";
        var removed = plan.Remove.Select(s => s.index).ToList();
        var remux = await _runner.RunFfmpeg(FfmpegArgs.Remux(video, temp, "matroska", removed, plan.DefaultChanges()), TestContext.Current.CancellationToken);
        Assert.True(remux.Success, remux.StdErr);

        var after = await Probe(temp);
        Assert.Null(RemuxVerifier.Verify(before, after, plan.Remove.Count, new FileInfo(video).Length, new FileInfo(temp).Length));
        Assert.Null(RemuxVerifier.CheckDefault(after, FfmpegArgs.OutputIndex(plan.NewDefault.index, removed)));
        var subs = after.streams.Where(s => s.IsSubtitle).ToList();
        Assert.True(subs[0].IsHearingImpaired);
        Assert.False(subs[0].Disposition("default"));
    }

    [Fact]
    public async Task Guesses_untagged_subtitle_languages_and_writes_the_kept_ones_into_the_video()
    {
        RequireFfmpeg();
        var ct = TestContext.Current.CancellationToken;
        var video = Path.Combine(_dir, "Guess Test.mkv");
        var dialogue = new[]
        {
            "I don't know what you want from me. We have to go there right now, okay? Yeah, I think that is the right thing to do. You can get the car and we will meet at the house. They are not going to wait for us all night, so just be ready.",
            "Je ne sais pas ce que tu veux. Nous devons partir maintenant, d'accord ? Oui, je pense que c'est la bonne chose à faire. Tu peux prendre la voiture et nous allons nous retrouver à la maison. Ils ne vont pas attendre toute la nuit, alors sois prêt avec moi."
        };
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=3:size=160x120:rate=10" };
        for (var i = 0; i < dialogue.Length; i++)
        {
            var srt = Path.Combine(_dir, $"guess{i}.srt");
            File.WriteAllText(srt, Srt(dialogue[i]));
            args.AddRange(new[] { "-i", srt });
        }
        args.AddRange(new[] { "-map", "0:v", "-map", "1:s", "-map", "2:s", "-c:v", "mpeg4", "-c:s", "srt", video });
        var made = await _runner.RunFfmpeg(args, ct);
        Assert.True(made.Success, made.StdErr);

        var before = await Probe(video);
        var guesses = await SubtitleLanguageGuessing.Run(_runner, video, before, ct);
        Assert.Equal(new[] { "eng", "fre" }, guesses.Select(g => g.Guess.Language));

        var plan = SubtitlePlanner.Plan(before, new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng") }, video);
        Assert.Equal(new[] { 2 }, plan.Remove.Select(s => s.index));

        var temp = video + ".subremover-tmp";
        var removed = plan.Remove.Select(s => s.index).ToList();
        var remux = await _runner.RunFfmpeg(FfmpegArgs.Remux(video, temp, "matroska", removed, plan.DefaultChanges(), plan.LanguageChanges()), ct);
        Assert.True(remux.Success, remux.StdErr);

        var after = await Probe(temp);
        Assert.Null(RemuxVerifier.Verify(before, after, plan.Remove.Count, new FileInfo(video).Length, new FileInfo(temp).Length));
        Assert.Null(RemuxVerifier.CheckLanguages(after, new Dictionary<int, string> { [1] = "eng" }));
    }

    [Fact]
    public async Task Extracts_a_text_subtitle_to_an_srt_file()
    {
        RequireFfmpeg();
        var video = await MakeSampleVideo("Extract Test.mkv");
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("fre"), KeepUndefined = false, ExtractTextSubtitles = true };
        var plan = SubtitlePlanner.Plan(await Probe(video), settings, video);
        var job = Assert.Single(plan.Extract);

        var result = await _runner.RunFfmpeg(FfmpegArgs.Extract(video, job.Stream.index, job.OutputPath, job.ConvertToSrt), TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.StdErr);
        Assert.EndsWith("Extract Test.fre.srt", job.OutputPath);
        Assert.Contains("Bonjour", File.ReadAllText(job.OutputPath));
    }

    [Fact]
    public async Task Ocrs_picture_subtitles_to_a_pgs_marked_srt()
    {
        RequireFfmpeg();
        var toolsDir = Path.GetDirectoryName(FindFfmpegFolder());
        if (!File.Exists(Path.Combine(toolsDir, "tessdata", "eng.traineddata")))
            Assert.Skip("tools/tessdata/eng.traineddata not found");

        var srt = Path.Combine(_dir, "lines.srt");
        File.WriteAllText(srt, Srt("I'll be back in 10 minutes.") + "2\n00:00:02,500 --> 00:00:04,000\n\"Illegal,\" said Lily.\n\n");
        var video = Path.Combine(_dir, "Picture Subs.mkv");
        var make = await _runner.RunFfmpeg(new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "color=c=0x203040:s=1920x1080:r=24:d=5", "-i", srt,
            "-filter_complex", "[1:s]text2graphicsub=s=1920x1080[sub]",
            "-map", "0:v", "-map", "[sub]", "-c:v", "mpeg4", "-c:s", "dvdsub", "-metadata:s:s:0", "language=eng", video
        }, TestContext.Current.CancellationToken);
        Assert.True(make.Success, make.StdErr);

        var plan = SubtitlePlanner.Plan(await Probe(video), new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("eng"), OcrPictureSubtitles = true }, video);
        var job = Assert.Single(plan.Ocr);
        Assert.EndsWith("Picture Subs.dvd.eng.srt", job.OutputPath);

        var problem = await SubtitleOcr.Run(_runner, new Tessdata(toolsDir), video, job, TestContext.Current.CancellationToken);

        Assert.Null(problem);
        var text = File.ReadAllText(job.OutputPath);
        Assert.Contains("I'll be back in 10 minutes.", text);
        Assert.Contains("\"Illegal,\" said Lily.", text);
        Assert.Contains("00:00:00,500 -->", text);
    }

    [Fact]
    public async Task A_video_cut_off_mid_read_is_caught_even_though_ffmpeg_reports_success()
    {
        RequireFfmpeg();
        var ct = TestContext.Current.CancellationToken;
        var srt = Path.Combine(_dir, "many.srt");
        File.WriteAllText(srt, string.Concat(Enumerable.Range(0, 20).Select(i =>
            $"{i + 1}\n00:00:{i:00},100 --> 00:00:{i:00},900\nLine {i + 1}\n\n")));
        var full = Path.Combine(_dir, "Full.mkv");
        var make = await _runner.RunFfmpeg(new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=21:size=320x240:rate=10", "-i", srt,
            "-map", "0:v", "-map", "1:s", "-c:v", "mpeg4", "-q:v", "3", "-c:s", "srt",
            "-metadata:s:s:0", "language=eng", full
        }, ct);
        Assert.True(make.Success, make.StdErr);

        // Same as a network drop: the reader only gets the first half of the file.
        var cut = Path.Combine(_dir, "Cut.mkv");
        var bytes = File.ReadAllBytes(full);
        File.WriteAllBytes(cut, bytes.Take(bytes.Length / 2).ToArray());
        var before = await Probe(full);
        var track = before.streams.Single(s => s.IsSubtitle);

        var extractTo = Path.Combine(_dir, "Cut.eng.srt.subremover-tmp");
        var extract = await _runner.RunFfmpeg(FfmpegArgs.Extract(cut, track.index, extractTo, convertToSrt: true), ct);
        Assert.True(extract.Success, "ffmpeg is expected to report success here; that's the problem being guarded against");
        var extractProblem = OutputChecks.TruncationMessage(extract.StdErr) ?? OutputChecks.CheckSubtitleFile(extractTo, track, isAss: false);
        Assert.NotNull(extractProblem);

        var remuxTo = Path.Combine(_dir, "Cut.mkv.subremover-tmp");
        var remux = await _runner.RunFfmpeg(FfmpegArgs.Remux(cut, remuxTo, "matroska", Array.Empty<int>()), ct);
        var remuxProblem = OutputChecks.TruncationMessage(remux.StdErr)
                           ?? RemuxVerifier.Verify(before, await Probe(remuxTo), 0, bytes.Length, new FileInfo(remuxTo).Length);
        Assert.NotNull(remuxProblem);
    }

    [Fact]
    public async Task Reads_accented_track_titles_correctly()
    {
        RequireFfmpeg();
        var srt = Path.Combine(_dir, "es.srt");
        File.WriteAllText(srt, Srt("Hola"));
        var video = Path.Combine(_dir, "Titles.mkv");
        var make = await _runner.RunFfmpeg(new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=160x120:r=10:d=2", "-i", srt,
            "-map", "0:v", "-map", "1:s", "-c:v", "mpeg4", "-c:s", "srt",
            "-metadata:s:s:0", "language=spa", "-metadata:s:s:0", "title=Español (Latinoamérica)", video
        }, TestContext.Current.CancellationToken);
        Assert.True(make.Success, make.StdErr);

        var sub = (await Probe(video)).streams.Single(s => s.IsSubtitle);

        Assert.Equal("Español (Latinoamérica)", sub.Title);
    }

    [Fact]
    public async Task Handles_paths_with_spaces_quotes_and_unicode()
    {
        RequireFfmpeg();
        var video = await MakeSampleVideo("Amélie's \"Fabuleux\" Destin.mkv".Replace("\"", OperatingSystem.IsWindows() ? "" : "\""));
        var probe = await Probe(video);
        Assert.Equal(1, probe.Count("video"));
    }
}
