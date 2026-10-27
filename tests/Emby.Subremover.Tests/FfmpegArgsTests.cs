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
