using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData("eng", "eng")]
    [InlineData("en", "eng")]
    [InlineData("ENG", "eng")]
    [InlineData("deu", "ger")]
    [InlineData("de", "ger")]
    [InlineData("fra", "fre")]
    [InlineData("zho", "chi")]
    [InlineData("pt-BR", "por")]
    [InlineData("en_US", "eng")]
    [InlineData("nob", "nor")]
    [InlineData("", "und")]
    [InlineData(null, "und")]
    [InlineData("qaa", "qaa")]
    public void Normalizes_every_spelling_to_one_code(string input, string expected)
    {
        Assert.Equal(expected, Languages.Normalize(input));
    }

    [Fact]
    public void Checked_German_keeps_tracks_tagged_deu_and_de()
    {
        var settings = new PlannerSettings { KeepLanguages = PlannerSettings.ParseLanguages("ger") };

        var plan = SubtitlePlanner.Plan(Movie(Sub(2, "deu"), Sub(3, "de"), Sub(4, "ger"), Sub(5, "fra")), settings, "/m/a.mkv");

        Assert.Equal(new[] { 2, 3, 4 }, plan.Keep.Select(s => s.index));
        Assert.Equal(new[] { 5 }, plan.Remove.Select(s => s.index));
    }

    [Fact]
    public void Standard_list_has_unique_codes_and_aliases()
    {
        var spellings = Languages.All.SelectMany(l => l.Aliases.Prepend(l.Code)).ToList();
        Assert.Equal(spellings.Count, spellings.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Logo_is_embedded_in_the_plugin()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Emby.Subremover.thumb.png");
        Assert.NotNull(stream);
        var header = new byte[4];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, header);
    }

    [Fact]
    public void Choices_keep_custom_codes_that_were_saved_before()
    {
        var choices = Plugin.LanguageChoicesFor("eng,qaa");

        Assert.Contains(choices, c => c.Value == "eng" && c.Name == "English (eng)");
        Assert.Contains(choices, c => c.Value == "qaa");
        Assert.Equal(Languages.All.Count + 1, choices.Count);
    }
}
