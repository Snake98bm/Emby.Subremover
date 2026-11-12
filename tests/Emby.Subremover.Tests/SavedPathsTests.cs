using System.IO;
using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class SavedPathsTests
{
    [Theory]
    [InlineData(@"\\nas\Emby\My Movie (2020).eng.srt", @"\\nas\Emby\My Movie (2020).eng.srt")]
    [InlineData(@"\\\\nas\\Emby\\My Movie (2020).eng.srt", @"\\nas\Emby\My Movie (2020).eng.srt")]
    [InlineData(@"\\\\\\\\\\\\\\\\nas\\\\\\\\Emby\\\\\\\\x.srt", @"\\nas\Emby\x.srt")]
    [InlineData(@"C:\\\\Media\\\\x.srt", @"C:\Media\x.srt")]
    [InlineData("/mnt/media/x.srt", "/mnt/media/x.srt")]
    public void Mends_doubled_backslashes(string spoiled, string mended)
    {
        Assert.Equal(mended, SavedPaths.Mend(spoiled));
    }

    [Fact]
    public void Shrinks_spoiled_json_and_leaves_good_json_alone()
    {
        // As saved after two spoiled round trips; the other strings were saved correctly.
        var spoiled = "{\"Processed\":{\"\\\\\\\\nas\\\\a.mkv\":{\"Size\":1}},\"Note\":\"say \\\"hi\\\"\\n\"," +
                      "\"MadeFiles\":[\"" + new string('\\', 16) + "nas" + new string('\\', 8) + "a.srt\"]}";
        var good = "{\"Processed\":{\"\\\\\\\\nas\\\\a.mkv\":{\"Size\":1}},\"Note\":\"say \\\"hi\\\"\\n\"," +
                   "\"MadeFiles\":[\"\\\\\\\\nas\\\\a.srt\"]}";
        Assert.Equal(good, Shrink(spoiled));
        Assert.Equal(good, Shrink(good));
    }

    [Fact]
    public void The_saved_list_round_trips_and_mends_old_entries()
    {
        var state = new SubremoverState { MadeFiles = new() { @"\\\\nas\\Emby\\a.srt" } };
        state.MadeFileSet().Add(@"\\nas\Emby\b.srt");
        Assert.Contains(@"\\NAS\Emby\A.srt", state.MadeFileSet());
        Assert.Equal(2, state.MadeFiles.Count);
        var copy = new TestJsonSerializer().DeserializeFromString<SubremoverState>(new TestJsonSerializer().SerializeToString(state));
        Assert.Contains(@"\\nas\Emby\b.srt", copy.MadeFileSet());
    }

    private static string Shrink(string json)
    {
        var writer = new StringWriter();
        SavedPaths.ShrinkJson(new StringReader(json), writer);
        return writer.ToString();
    }
}
