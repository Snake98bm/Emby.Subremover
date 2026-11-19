using System.Text;
using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class SubtitleTextCheckTests
{
    private static readonly string[] Lines =
    {
        "what are you doing here tonight", "we have to get out of the house now", "did you brush your teeth",
        "she said the car would be here soon", "they never told us about the money", "i think we should go back home",
        "where did you put the keys", "this is the best day of my life", "you have no idea what you did",
        "come on we are going to be late"
    };

    // An .srt with one cue per line; every "garbleEvery"-th line gets a word that isn't English in the middle.
    private static string Srt(int cues, int garbleEvery = 0, string foreign = "sehingga", bool misreadI = false)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cues; i++)
        {
            var line = Lines[i % Lines.Length];
            if (garbleEvery > 0 && i % garbleEvery == 0)
            {
                var words = line.Split(' ');
                words[words.Length / 2] = foreign;
                line = string.Join(" ", words);
            }
            if (misreadI && i % 3 == 0) line = "l'm sure " + line;
            sb.Append(i + 1).Append("\r\n00:00:0").Append(i % 10).Append(",000 --> 00:00:0").Append(i % 10).Append(",900\r\n")
              .Append(line).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Plain_English_passes()
    {
        Assert.Null(SubtitleTextCheck.WhyNot(Srt(300), "eng"));
        Assert.Null(SubtitleTextCheck.WhyNot(Srt(300), "en"));
    }

    [Fact]
    public void A_foreign_word_here_and_there_passes()
    {
        // A name or a line of another language every 20 lines: 5%.
        Assert.Null(SubtitleTextCheck.WhyNot(Srt(300, garbleEvery: 20), "eng"));
    }

    [Fact]
    public void Machine_translated_English_is_turned_down()
    {
        // Every 4th line has a foreign word in it: 25%, like the garbled download that had 19%.
        var why = SubtitleTextCheck.WhyNot(Srt(300, garbleEvery: 4), "eng");
        Assert.NotNull(why);
        Assert.Contains("machine-translated", why);
    }

    [Fact]
    public void Ocr_misreads_of_I_count_as_English()
    {
        Assert.Null(SubtitleTextCheck.WhyNot(Srt(300, misreadI: true), "eng"));
    }

    [Fact]
    public void Too_short_a_file_isnt_judged()
    {
        Assert.Null(SubtitleTextCheck.WhyNot(Srt(50, garbleEvery: 2), "eng"));
    }

    [Fact]
    public void Another_language_tagged_English_is_turned_down()
    {
        var spanish = new StringBuilder();
        for (var i = 0; i < 200; i++)
            spanish.Append(i + 1).Append("\r\n00:00:01,000 --> 00:00:02,000\r\n¿Qué estás haciendo aquí esta noche? No lo sé, pero tenemos que irnos.\r\n\r\n");
        var why = SubtitleTextCheck.WhyNot(spanish.ToString(), "eng");
        Assert.NotNull(why);
        Assert.Contains("spa", why);
    }
}
