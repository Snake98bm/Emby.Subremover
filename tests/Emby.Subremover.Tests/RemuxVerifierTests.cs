using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class RemuxVerifierTests
{
    private static readonly ProbeResult Before = Movie(Sub(2, "eng"), Sub(3, "fre"));

    [Fact]
    public void Accepts_a_correct_remux()
    {
        Assert.Null(RemuxVerifier.Verify(Before, Movie(Sub(2, "eng")), 1, 1000, 990));
    }

    [Fact]
    public void Rejects_a_lost_audio_track()
    {
        var after = Movie(Sub(2, "eng"));
        after.streams.RemoveAll(s => s.codec_type == "audio");
        Assert.Contains("audio", RemuxVerifier.Verify(Before, after, 1, 1000, 990));
    }

    [Fact]
    public void Rejects_wrong_subtitle_count()
    {
        Assert.Contains("subtitle", RemuxVerifier.Verify(Before, Movie(Sub(2, "eng"), Sub(3, "fre")), 1, 1000, 990));
    }

    [Fact]
    public void Rejects_a_truncated_file()
    {
        var after = Movie(Sub(2, "eng"));
        after.format.duration = "2700";
        Assert.Contains("duration", RemuxVerifier.Verify(Before, after, 1, 1000, 990));
        Assert.Contains("size", RemuxVerifier.Verify(Before, Movie(Sub(2, "eng")), 1, 1000, 400));
    }

    [Fact]
    public void Rejects_an_empty_probe()
    {
        Assert.NotNull(RemuxVerifier.Verify(Before, new ProbeResult(), 1, 1000, 990));
        Assert.NotNull(RemuxVerifier.Verify(Before, null, 1, 1000, 990));
    }
}
