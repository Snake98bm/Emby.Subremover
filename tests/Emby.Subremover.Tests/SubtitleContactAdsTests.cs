using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class SubtitleContactAdsTests
{
    private static string Match(string line, bool credits = true) => new SubtitleAdRules("", credits).Match(line);

    [Theory]
    [InlineData("© SoMe NaMe ™ Mobile - +919815899536 EMail - someone123@hotmail.com")]
    [InlineData("c SoMe NaMe T Mobile - +919815899536 EMail - someone123@hotmail.com")]
    [InlineData("Contact: uploader.name@gmail.com")]
    [InlineData("WhatsApp +44 7911 123456 for more")]
    [InlineData("(hamsuter@nate.com)")]
    public void Uploader_contact_lines_are_ads(string line)
    {
        Assert.Equal("contact details", Match(line));
    }

    [Theory]
    [InlineData("Call me at 555-1234.")]
    [InlineData("It's 2 + 2, Tom.")]
    [InlineData("Meet me @ the bar at 9.")]
    [InlineData("The year was 1999, plus 1000000 people came.")]
    [InlineData("We're at +5 degrees.")]
    public void Dialogue_with_numbers_or_at_signs_stays(string line)
    {
        Assert.Null(Match(line));
    }

    [Fact]
    public void Contact_lines_go_with_the_credits_switch()
    {
        Assert.Null(Match("Contact: uploader.name@gmail.com", credits: false));
    }
}
