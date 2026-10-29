using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class FfmpegArgsTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Movies\A B\", "\"C:\\Movies\\A B\\\\\"")]
    [InlineData(@"C:\NoSpaces\file.mkv", @"C:\NoSpaces\file.mkv")]
    public void Quotes_arguments_safely(string input, string expected)
    {
        Assert.Equal(expected, FfmpegArgs.Quote(input));
    }

    [Fact]
    public void Remux_keeps_everything_except_the_removed_streams_in_order()
    {
        var args = FfmpegArgs.Remux("in.mkv", "out.tmp", "matroska", new[] { 7, 3 });
        var line = string.Join(" ", args);

        Assert.Contains("-i in.mkv -map 0 -map -0:3 -map -0:7 -c copy", line);
        Assert.EndsWith("-f matroska out.tmp", line);
    }

    [Fact]
    public void Remux_changes_default_flags_by_their_index_in_the_new_file()
    {
        var args = FfmpegArgs.Remux("in.mkv", "out.tmp", "matroska", new[] { 2 }, new Dictionary<int, bool> { [4] = true, [3] = false });
        var line = string.Join(" ", args);

        Assert.Contains("-map_chapters 0 -disposition:2 -default -disposition:3 +default -max_muxing_queue_size", line);
    }

    [Fact]
    public void Remux_writes_guessed_languages_by_their_index_in_the_new_file()
    {
        var args = FfmpegArgs.Remux("in.mkv", "out.tmp", "matroska", new[] { 2 }, null, new Dictionary<int, string> { [4] = "fre" });

        Assert.Contains("-metadata:s:3 language=fre", string.Join(" ", args));
    }

    [Fact]
    public void Reads_the_start_of_several_subtitle_tracks_in_one_pass()
    {
        var line = string.Join(" ", FfmpegArgs.ReadSubtitleText("in.mkv", new[] { 3, 5 }, new[] { "a.srt", "b.srt" }, 900));

        Assert.EndsWith("-i in.mkv -map 0:3 -c:s srt -t 900 -f srt a.srt -map 0:5 -c:s srt -t 900 -f srt b.srt", line);
    }

    [Theory]
    [InlineData("a.mkv", "matroska")]
    [InlineData("a.MP4", "mp4")]
    [InlineData("a.m2ts", "mpegts")]
    [InlineData("a.iso", null)]
    [InlineData("a.strm", null)]
    public void Picks_the_muxer_from_the_extension(string path, string expected)
    {
        Assert.Equal(expected, FfmpegArgs.MuxerFor(path));
    }
}
