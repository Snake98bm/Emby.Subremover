using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class LogFilterTests
{
    private const string Log =
        "2026-11-16 05:00:00.010 Info  SubRemover 1.0.2: starting. Details: subremover-x.txt\n" +
        "2026-11-16 05:00:00.020 Info  SubRemover settings for this run:\n" +
        "    Remove ads from subtitle files: on\n" +
        "    Download hash-matched subtitles before changing a video: on\n" +
        "2026-11-16 05:00:01.000 Info  SubRemover: removed 3 subtitle tracks from D:\\Movies\\My Movie (2020)\\My Movie (2020).mkv\n" +
        "2026-11-16 05:00:02.000 Debug SubRemover: nothing to do for D:\\Movies\\Other (2019)\\Other (2019).mkv\n" +
        "2026-11-16 05:00:03.000 Info  SubRemover: subtitle timing of D:\\Movies\\My Movie (2020)\\My Movie (2020).mkv (95 min of audio):\n" +
        "    My Movie (2020).en.srt: off by +2.00 s, moving every cue -2.00 s\n" +
        "2026-11-16 05:00:04.000 Debug SubRemover: no hash-matched eng subtitle for D:\\Movies\\Other (2019)\\Other (2019).mkv (hash 0123)\n" +
        "2026-11-16 05:00:05.000 Info  SubRemover: OCR of track 3 of D:\\Movies\\My Movie (2020)\\My Movie (2020).mkv -> My Movie (2020).en.srt\n" +
        "2026-11-16 05:00:06.000 Warn  SubRemover: couldn't read D:\\Movies\\Bad (2001)\\Bad (2001).mkv\n" +
        "2026-11-16 05:00:07.000 Info  SubRemover finished: 1 changed, 0 would change\n";

    [Fact]
    public void Each_kind_shows_only_its_own_entries_with_their_indented_lines()
    {
        var srt = LogFilter.Apply(Log, "SubtitleFiles", false).Text;
        Assert.Contains("subtitle timing of", srt);
        Assert.Contains("moving every cue -2.00 s", srt);
        Assert.DoesNotContain("removed 3 subtitle tracks", srt);
        Assert.DoesNotContain("settings for this run", srt);     // the settings list names subtitle files, but isn't about them

        var videos = LogFilter.Apply(Log, "Videos", false).Text;
        Assert.Contains("removed 3 subtitle tracks", videos);
        Assert.Contains("nothing to do for", videos);
        Assert.DoesNotContain("no hash-matched", videos);          // hash look-ups are only that
        Assert.DoesNotContain("OCR of track", videos);

        Assert.Contains("no hash-matched", LogFilter.Apply(Log, "HashDownloads", false).Text);
        Assert.Contains("OCR of track", LogFilter.Apply(Log, "Ocr", false).Text);
        Assert.Equal("2026-11-16 05:00:06.000 Warn  SubRemover: couldn't read D:\\Movies\\Bad (2001)\\Bad (2001).mkv",
            LogFilter.Apply(Log, "problems", false).Text);
    }

    [Fact]
    public void Everything_keeps_the_whole_log_and_hiding_details_drops_debug_lines()
    {
        var all = LogFilter.Apply(Log, "all", false);
        Assert.Equal(9, all.Counts["all"]);
        Assert.Contains("settings for this run", all.Text);

        var brief = LogFilter.Apply(Log, null, true);
        Assert.Equal(7, brief.Counts["all"]);
        Assert.DoesNotContain("nothing to do for", brief.Text);
        Assert.Equal(0, brief.Counts["HashDownloads"]);
        Assert.Equal(1, brief.Counts["problems"]);
    }

    [Fact]
    public void Nothing_of_a_kind_says_so()
    {
        Assert.Equal("Nothing of this kind in this log.", LogFilter.Apply("2026-11-16 05:00:00.010 Info  SubRemover: hi\n", "Ocr", false).Text);
    }
}
