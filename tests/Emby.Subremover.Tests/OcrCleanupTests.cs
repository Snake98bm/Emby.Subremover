using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class OcrCleanupTests
{
    // What ffmpeg's OCR writes for Blu-ray subtitles: every cue lasts 1193 hours, and an empty cue marks the clear.
    private const string PgsOcr =
        "1\n00:01:17,035 --> 1193:04:04,330\nHello there.\n\n" +
        "2\n00:01:19,621 --> 1193:04:06,916\n\n\n" +
        "3\n00:01:36,680 --> 1193:04:23,975\nSecond line.\n\n" +
        "4\n00:01:41,560 --> 1193:04:28,855\n\n\n" +
        "5\n00:01:42,000 --> 1193:04:29,000\nLast one.\n\n";

    [Fact]
    public void Picture_subtitles_end_when_the_screen_clears_and_the_empty_cues_are_gone()
    {
        var cues = OcrCleanup.Parse(OcrCleanup.Clean(PgsOcr, "eng"));

        Assert.Equal(3, cues.Count);
        Assert.Equal(77035, cues[0].StartMs);
        Assert.Equal(79621, cues[0].EndMs);
        Assert.Equal(101560, cues[1].EndMs);
        Assert.Equal("Second line.", Assert.Single(cues[1].Lines));
    }

    [Fact]
    public void The_last_cue_gets_a_few_seconds_when_nothing_clears_it()
    {
        var cues = OcrCleanup.Parse(OcrCleanup.Clean(PgsOcr, "eng"));
        Assert.Equal(102000 + 4000, cues[2].EndMs);
    }

    [Fact]
    public void The_same_picture_sent_again_a_frame_later_is_one_subtitle()
    {
        const string repeat =
            "1\n00:37:20,280 --> 1193:40:07,575\nAre you sure?\n\n" +
            "2\n00:37:20,321 --> 1193:40:07,616\nAre you sure?\n\n" +
            "3\n00:37:23,324 --> 1193:40:10,619\n\n\n" +
            "4\n00:37:23,408 --> 1193:40:10,703\nAre you sure?\n\n" +
            "5\n00:37:25,000 --> 1193:40:12,000\n\n\n";
        var cues = OcrCleanup.Parse(OcrCleanup.Clean(repeat, "eng"));

        Assert.Equal(2, cues.Count);
        Assert.Equal((20280L + 37 * 60000, 23324L + 37 * 60000), (cues[0].StartMs, cues[0].EndMs));
        Assert.Equal(23408L + 37 * 60000, cues[1].StartMs);
    }

    [Fact]
    public void Cues_with_real_end_times_are_left_alone()
    {
        const string dvd = "1\n00:00:01,000 --> 00:00:02,500\nOne.\n\n2\n00:00:10,000 --> 00:00:12,000\nTwo.\n\n";
        var cues = OcrCleanup.Parse(OcrCleanup.Clean(dvd, "fre"));
        Assert.Equal(2500, cues[0].EndMs);
        Assert.Equal(12000, cues[1].EndMs);
    }

    [Fact]
    public void Windows_line_endings_are_read()
    {
        var cues = OcrCleanup.Parse(PgsOcr.Replace("\n", "\r\n"));
        Assert.Equal(5, cues.Count);
        Assert.Equal("Hello there.", Assert.Single(cues[0].Lines));
    }

    [Theory]
    [InlineData("1 don't know.", "I don't know.")]
    [InlineData("l think so.", "I think so.")]
    [InlineData("/ want to go.", "I want to go.")]
    [InlineData("and ! can't stay.", "and I can't stay.")]
    [InlineData("Well, 1 guess", "Well, I guess")]
    [InlineData("- 1 have to see it.", "- I have to see it.")]
    [InlineData("1'll be there.", "I'll be there.")]
    [InlineData("/'m tired.", "I'm tired.")]
    [InlineData("['m tired.", "I'm tired.")]
    [InlineData("[ve told him.", "I've told him.")]
    [InlineData("[t's late.", "It's late.")]
    [InlineData("lt is.", "It is.")]
    [InlineData("/f you go", "If you go")]
    [InlineData("what the plan /s?", "what the plan is?")]
    [InlineData("[ just want to help.", "I just want to help.")]
    [InlineData("No, ] don't care.", "No, I don't care.")]
    [InlineData("and !] can't stay.", "and I can't stay.")]
    [InlineData("his 1.Q. is high", "his I.Q. is high")]
    [InlineData("1 don't Know.", "I don't know.")]
    [InlineData("[I'm not so sure.", "I'm not so sure.")]
    [InlineData("Why should I! know?", "Why should I know?")]
    [InlineData("War Is part of nature.", "War is part of nature.")]
    [InlineData("What Is this?", "What is this?")]
    [InlineData("That's what] saw.", "That's what I saw.")]
    public void I_misreads_are_fixed(string ocr, string expected)
    {
        Assert.Equal(expected, CleanLine(ocr));
    }

    [Theory]
    [InlineData("1 of them is gone.")]
    [InlineData("It took 1 minute.")]
    [InlineData("Call 1-3-7-5.")]
    [InlineData("I know Kevin.")]
    [InlineData("Wait! Stop.")]
    public void Real_text_is_left_alone(string text)
    {
        Assert.Equal(text, CleanLine(text));
    }

    [Theory]
    [InlineData("[ Typing /", "[ Typing ]")]
    [InlineData("[ Door Opens", "[ Door Opens ]")]
    [InlineData("[ Bell", "[ Bell ]")]
    [InlineData("[ Bell Tolls l", "[ Bell Tolls ]")]
    [InlineData("[Door Opens", "[Door Opens]")]
    [InlineData("Door Opens ]", "[ Door Opens ]")]
    [InlineData("Door Opens]", "[Door Opens]")]
    [InlineData("|Door Opens]", "[Door Opens]")]
    [InlineData("(Sighs]", "[Sighs]")]
    [InlineData("[Sighs)", "[Sighs]")]
    [InlineData("[ Man Screaming /]", "[ Man Screaming ]")]
    [InlineData("[Man Screaming /]", "[Man Screaming]")]
    [InlineData("- Door Opens ]", "- [ Door Opens ]")]
    [InlineData("Whispering)", "(Whispering)")]
    public void Lost_and_misread_brackets_are_fixed(string ocr, string expected)
    {
        Assert.Equal(expected, CleanLine(ocr));
    }

    [Fact]
    public void A_bracket_closed_on_the_next_line_is_left_open()
    {
        var cues = OcrCleanup.Parse(OcrCleanup.Clean("1\n00:00:01,000 --> 1193:00:00,000\n[ Man\nOn Radio ]\n\n", "eng"));
        Assert.Equal(new[] { "[ Man", "On Radio ]" }, cues[0].Lines);
    }

    [Theory]
    [InlineData("You dont say.", "You don't say.")]
    [InlineData("They wi1l come.", "They will come.")]
    [InlineData("Say  it.", "Say it.")]
    [InlineData("-»* [ Woman Singing ]", "♪ [ Woman Singing ]")]
    [InlineData("J\" [ Singing Continues ]", "♪ [ Singing Continues ]")]
    [InlineData("* [ Singing Continues ]", "♪ [ Singing Continues ]")]
    [InlineData("_ /f the river runs »", "♪ If the river runs ♪")]
    [InlineData("- Over here!", "- Over here!")]
    [InlineData("f It'll soon be morning", "♪ It'll soon be morning")]
    [InlineData("- ¢ Such an easy mood", "- ♪ Such an easy mood")]
    [InlineData("J. Edgar said so.", "J. Edgar said so.")]
    public void Common_word_misreads_are_fixed(string ocr, string expected)
    {
        Assert.Equal(expected, CleanLine(ocr));
    }

    [Fact]
    public void Text_in_other_languages_is_not_changed()
    {
        var cues = OcrCleanup.Parse(OcrCleanup.Clean("1\n00:00:01,000 --> 00:00:02,000\nl est 1 heure.\n\n", "fre"));
        Assert.Equal("l est 1 heure.", Assert.Single(cues[0].Lines));
    }

    [Theory]
    [InlineData("will commence...", "Will COMMEeNCE...")]
    [InlineData("Get down to the beach.", "Get down fo the beach.")]
    [InlineData("Hopefully, they will come.", "Hopetully, they will come.")]
    [InlineData("You landed in an escape pod.", "You landed inan escape pod.")]
    [InlineData("Yes. I have to be sure", "Yes. have to be sure")]
    public void Both_models_keep_the_better_reading(string good, string bad)
    {
        // Whichever model got it right, the right one is kept.
        Assert.Equal(good, PickBoth(good, bad));
        Assert.Equal(good, PickBoth(bad, good));
    }

    [Fact]
    public void Both_models_mind_capitals_and_dont_trust_a_misread_one_model_repeats()
    {
        var fast = "1\n00:00:01,000 --> 1193:00:00,000\nIt's late.\n\n2\n00:00:02,000 --> 1193:00:00,000\n\n\n" +
                   "3\n00:00:03,000 --> 1193:00:00,000\nsorry?\n\n4\n00:00:04,000 --> 1193:00:00,000\n\n\n" +
                   "5\n00:00:05,000 --> 1193:00:00,000\nI like Opening shopping malls.\n\n6\n00:00:06,000 --> 1193:00:00,000\n\n\n" +
                   "7\n00:00:07,000 --> 1193:00:00,000\nIl find it frightening.\n\n8\n00:00:08,000 --> 1193:00:00,000\n\n\n" +
                   "9\n00:00:09,000 --> 1193:00:00,000\nIl saw it.\n\n10\n00:00:10,000 --> 1193:00:00,000\n\n\n";
        var best = fast.Replace("sorry?", "Sorry?").Replace("Opening", "opening").Replace("Il ", "l ");
        var lines = OcrCleanup.Parse(OcrCleanup.CleanBoth(fast, best, "eng")).Select(c => Assert.Single(c.Lines)).ToList();
        Assert.Equal(new[] { "It's late.", "Sorry?", "I like opening shopping malls.", "I find it frightening.", "I saw it." }, lines);
    }

    [Fact]
    public void Both_models_choose_line_by_line()
    {
        var fast = "1\n00:00:01,000 --> 1193:00:00,000\nBut without the benetit\nof proper laboratory tests,\n\n2\n00:00:05,000 --> 1193:00:00,000\n\n\n";
        var best = "1\n00:00:01,000 --> 1193:00:00,000\nBut without the benefit\nof proper laboratory fests,\n\n2\n00:00:05,000 --> 1193:00:00,000\n\n\n";
        var cue = Assert.Single(OcrCleanup.Parse(OcrCleanup.CleanBoth(fast, best, "eng")));
        Assert.Equal(new[] { "But without the benefit", "of proper laboratory tests," }, cue.Lines);
        Assert.Equal(5000, cue.EndMs);
    }

    [Fact]
    public void Names_both_models_agree_on_are_not_misreads()
    {
        Assert.Equal(0, OcrChooser.Penalty(new[] { "Marla, Tobin's goin' to the E.R." }, english: true,
            new Dictionary<string, int> { ["marla"] = 2, ["tobin's"] = 2 }));
        Assert.True(OcrChooser.Penalty(new[] { "Hopetully" }, english: true, new Dictionary<string, int> { ["hopetully"] = 1 }) > 0);
    }

    [Fact]
    public void The_word_list_is_built_in()
    {
        Assert.True(EnglishWords.Contains("Hopefully"));
        Assert.True(EnglishWords.Contains("don’t"));
        Assert.False(EnglishWords.Contains("hopetully"));
        Assert.False(EnglishWords.Contains("fo"));
    }

    private static string PickBoth(string first, string second)
    {
        string Srt(string line) => "1\n00:00:01,000 --> 1193:00:00,000\n" + line + "\n\n2\n00:00:03,000 --> 1193:00:00,000\n\n\n";
        return Assert.Single(Assert.Single(OcrCleanup.Parse(OcrCleanup.CleanBoth(Srt(first), Srt(second), "eng"))).Lines);
    }

    private static string CleanLine(string line)
    {
        var srt = "1\n00:00:01,000 --> 00:00:02,000\n" + line + "\n\n";
        return Assert.Single(Assert.Single(OcrCleanup.Parse(OcrCleanup.Clean(srt, "eng"))).Lines);
    }
}
