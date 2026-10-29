using Emby.Subremover.Core;
using Xunit;
using static Emby.Subremover.Tests.TestData;

namespace Emby.Subremover.Tests;

public class OcrOfferTests
{
    private const string Video = @"D:\Movies\My Movie (2020)\My Movie (2020).mkv";

    [Fact]
    public void Offers_picture_tracks_in_the_searched_language_and_those_without_one()
    {
        var probe = Movie(
            Sub(2, "eng", codec: "hdmv_pgs_subtitle", sdh: true),
            Sub(3, "eng"),
            Sub(4, "fre", codec: "hdmv_pgs_subtitle"),
            Sub(5, "und", codec: "dvd_subtitle"),
            Sub(6, "eng", codec: "hdmv_pgs_subtitle", title: "Commentary"));

        var offers = OcrOffer.For(probe, Video, "en");

        Assert.Equal(new[] { 2, 5 }, offers.Select(o => o.StreamIndex));
        Assert.All(offers, o => Assert.Equal("eng", o.TesseractLanguage));
        Assert.True(offers[0].IsHearingImpaired);
        Assert.StartsWith("OCR of the built-in subtitles #2 eng hdmv_pgs_subtitle SDH", offers[0].Description);
    }

    [Fact]
    public void Nothing_is_offered_for_a_language_ocr_cant_read()
    {
        Assert.Empty(OcrOffer.For(Movie(Sub(2, "qaa", codec: "hdmv_pgs_subtitle")), Video, "qaa"));
    }

    [Fact]
    public void The_id_emby_hands_back_finds_the_same_track()
    {
        var offer = new OcrOffer
        {
            VideoPath = @"\\nas\TV\My Show\My Show - S01E08 - Rain, Wind And Coffee | Part 2.mkv",
            StreamIndex = 5,
            Language = "eng",
            TesseractLanguage = "eng",
            IsForced = true
        };

        var back = OcrOffer.FromId(offer.ToId());

        Assert.Equal(offer.VideoPath, back.VideoPath);
        Assert.Equal(5, back.StreamIndex);
        Assert.True(back.IsForced);
        Assert.False(back.IsHearingImpaired);
        Assert.DoesNotContain("/", offer.ToId());
        Assert.Throws<ArgumentException>(() => OcrOffer.FromId(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("2|x"))));
    }
}
