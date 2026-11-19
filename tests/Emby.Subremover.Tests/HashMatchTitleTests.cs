using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class HashMatchTitleTests
{
    [Theory]
    [InlineData("My.Movie.2020.1080p.BluRay.x264-GROUP", "My Movie", 2020)]
    [InlineData("My Movie (2020) (1080p BluRay x265 HEVC 10bit AAC 5.1)", "My Movie", 2020)]
    [InlineData("The.Last.Movie.25th.Anniversary.Edition.1984.1080p", "The Last Movie", 1984)]
    [InlineData("My.Movie.Episode.VII.The.Next.One.2015.720p", "My Movie - The Next One", 2015)]
    [InlineData("My Movie Part 7 2015", "My Movie VII", 2015)]               // roman numerals count as numbers
    [InlineData("My.Movie.2019.WEB-DL", "My Movie", 2020)]                  // a year out is fine (release vs premiere)
    [InlineData("", "My Movie", 2020)]                                      // no name: nothing to go by
    public void A_name_for_the_same_title_passes(string release, string title, int? year)
    {
        Assert.Null(HashMatchTitle.WhyNotMovie(release, title, year));
    }

    [Theory]
    [InlineData("Other Film (2000)", "My Movie", 2020)]                     // another film entirely
    [InlineData("Some.Other.Film.2017.720p.HDRip", "The Girl in the Movie", 2016)]
    [InlineData("The.My.Movie.2014.720p.BluRay", "My Movie 2 - The Second Part", 2019)] // the first film for its sequel
    [InlineData("My.Movie.2008.1080p", "My Movie", 2020)]                    // same name, another year (a remake)
    public void A_name_for_another_title_is_turned_down(string release, string title, int? year)
    {
        Assert.NotNull(HashMatchTitle.WhyNotMovie(release, title, year));
    }

    [Theory]
    [InlineData("My.Show.S02E05.720p.WEB", 2, 5, true)]
    [InlineData("My Show 2x05 Title", 2, 5, true)]
    [InlineData("My.Show.S02E06.720p.WEB", 2, 5, false)]
    [InlineData("My.Show.S01E05.720p.WEB", 2, 5, false)]
    [InlineData("My Show - The Episode Title", 2, 5, true)]                  // says no episode: passes
    public void An_episode_name_must_not_say_another_episode(string release, int season, int episode, bool fits)
    {
        Assert.Equal(fits, HashMatchTitle.WhyNotEpisode(release, season, episode) == null);
    }
}
