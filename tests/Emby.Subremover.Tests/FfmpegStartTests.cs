using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

/// <summary>
/// A Fedora server couldn't start ffmpeg for OCR ("No such file or directory" with the data folder as working
/// directory, though the folder was there): that must be a failure with a reason, and OCR must have another way.
/// </summary>
public sealed class FfmpegStartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subremover-start-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // A program every test machine has, that does nothing and exits 0.
    private static string Harmless => OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/true";
    private static readonly string[] HarmlessArgs = { "/c", "exit", "0" };

    [Fact]
    public async Task A_program_that_is_not_there_is_a_failure_with_the_reason_not_an_exception()
    {
        var missing = Path.Combine(_dir, "no-such-ffmpeg");
        var runner = new FfmpegRunner(() => missing, () => missing);

        var result = await runner.RunFfmpeg(new[] { "-version" }, TestContext.Current.CancellationToken);

        Assert.True(result.StartFailed);
        Assert.False(result.Success);
        Assert.Contains("couldn't start " + missing, result.StdErr);
        Assert.Contains("program NOT found", result.StdErr);

        var probe = await runner.RunFfprobe(new[] { "-version" }, TestContext.Current.CancellationToken);
        Assert.True(probe.StartFailed);
    }

    [Fact]
    public async Task A_working_folder_that_is_not_there_is_named_in_the_reason()
    {
        var runner = new FfmpegRunner(() => Harmless, () => Harmless);
        var gone = Path.Combine(_dir, "gone");

        var result = await runner.RunFfmpeg(HarmlessArgs, TestContext.Current.CancellationToken, gone);

        Assert.True(result.StartFailed);
        Assert.Contains("program found", result.StdErr);
        Assert.Contains(gone + " (folder NOT found)", result.StdErr);
    }

    [Fact]
    public async Task A_program_that_starts_is_not_a_start_failure()
    {
        var runner = new FfmpegRunner(() => Harmless, () => Harmless);
        var result = await runner.RunFfmpeg(HarmlessArgs, TestContext.Current.CancellationToken, _dir);
        Assert.False(result.StartFailed);
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("tessdata", "tessdata")]
    [InlineData("tessdata_best", "tessdata_best")]
    [InlineData("/var/lib/emby/data/subremover/tessdata", "/var/lib/emby/data/subremover/tessdata")]
    [InlineData(@"C:\ProgramData\Emby-Server\data\subremover\tessdata_best", @"C\\:/ProgramData/Emby-Server/data/subremover/tessdata_best")]
    [InlineData("/srv/emby's data/sub,remover/tessdata", @"/srv/emby\\\'s data/sub\,remover/tessdata")]
    public void A_whole_path_to_the_language_files_is_escaped_for_the_filter_graph(string folder, string inFilter)
    {
        Assert.Equal(inFilter, FfmpegArgs.FilterPath(folder));

        var filter = FfmpegArgs.Ocr("in.mkv", 3, "eng", "out.srt", folder)[8];
        Assert.Equal($"[0:3]graphicsub2text=tessdata_path={inFilter}/:language=eng:ocr_mode=lstm:recognize=italic[ocr]", filter);
    }
}
