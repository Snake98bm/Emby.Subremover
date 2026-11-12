using System.Text;
using Emby.Subremover.Core;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Subremover.Tests;

public sealed class SubtitleAdsTests
{
    private static readonly SubtitleAdRules Default = new(SubtitleAdRules.DefaultList);

    private static string Srt(params string[] cues) =>
        string.Concat(cues.Select((c, i) => $"{i + 1}\r\n00:00:{i:00},000 --> 00:00:{i:00},900\r\n{c.Replace("\n", "\r\n")}\r\n\r\n"));

    [Theory]
    [InlineData("Advertise your product or brand here contact www.OpenSubtitles.org today")]
    [InlineData("Support us and become VIP member to remove all ads from www.OpenSubtitles.org")]
    [InlineData("-== [ www.OpenSubtitles.com ] ==-")]
    [InlineData("Sync and corrections by someone www.addic7ed.com")]
    [InlineData("Watch Movies, TV Series and Live Sports Signup Here -> WWW.ADMITME.APP")]
    [InlineData("Downloaded From www.AllSubs.org")]
    [InlineData("Official YIFY movies site: YTS.MX")]
    [InlineData("Please rate this subtitle at www.osdb.link/abc12 Help other users to choose the best subtitles")]
    [InlineData("www.tvsubtitles.net")]
    [InlineData("Team W&G ~seriessub.com~")]
    public void Ads_from_real_files_are_found(string line) => Assert.NotNull(Default.Match(line));

    [Theory]
    [InlineData("Subtitles by someone")]
    [InlineData("== sync, corrected by someone == @someone")]
    [InlineData("- Synced and corrected by Someone -")]
    [InlineData("Resync By M_I_S")]
    [InlineData("Subtitle created by - Someone -")]
    [InlineData("Created and Encoded by -- Someone --")]
    [InlineData("synchro: someone")]
    [InlineData("Encoded by Someone")]
    [InlineData("Translated by Some Group")]
    [InlineData("Ripped by:")]
    [InlineData("Final edit by A.B. Someone")]
    [InlineData("Provided by someone123")]
    [InlineData("- Created by Someone and Resync by Other -")]
    [InlineData("timing and subtitle by someone")]
    public void Credits_are_found_at_the_start_of_a_line(string line) => Assert.Equal("credit", Default.Match(line));

    // A sentence carried over from the line before, or one about something being made: not a credit.
    [Theory]
    [InlineData("created by a big machine,")]
    [InlineData("created by what's called a rope trick.")]
    [InlineData("created by the city council.")]
    [InlineData("ripped apart piece by piece.")]
    [InlineData("created by Someone Famous.")]
    [InlineData("Created by the people who sell flowers and")]
    [InlineData("Encoded by using a secret key.")]
    [InlineData("- created by doing some digging")]
    public void Dialogue_with_credit_words_stays(string line) => Assert.Null(Default.Match(line));

    [Theory]
    [InlineData("He was corrected by his teacher.")]
    [InlineData("Awww. Ewww.")]
    [InlineData("Log on to discovery.com/myshow.")]
    [InlineData("you had free will, and look at what you did with it!")]
    [InlineData("I downloaded it from the server.")]
    [InlineData("That's a lot of bytes.")]
    [InlineData("Open the subtitles menu.")]
    public void Dialogue_stays(string line) => Assert.Null(Default.Match(line));

    [Fact]
    public void Zero_byte_padding_is_left_out_of_the_logged_text()
    {
        var result = SrtAdCleaner.Clean(Srt("Hello there.", "Subtitles by someone\n" + new string('\0', 500)), Default);
        var removed = Assert.Single(result.Removed);
        Assert.Equal("Subtitles by someone", removed.Text);
        Assert.Equal("Subtitles by someone", SrtAdCleaner.ForLog("Subtitles by someone /" + new string('\0', 50)).TrimEnd(' ', '/'));
        Assert.True(SrtAdCleaner.ForLog(new string('x', 1000)).Length <= 160);
    }

    [Fact]
    public void Caption_credits_only_go_with_their_switch()
    {
        const string line = "Captioned by Media Access Group at WGBH access.wgbh.org";
        Assert.Null(Default.Match(line));
        Assert.Equal("captioning credit", new SubtitleAdRules(SubtitleAdRules.DefaultList, true, true).Match(line));
        Assert.Null(new SubtitleAdRules(SubtitleAdRules.DefaultList, true, false).Match("Captioning sponsored by SOME TELEVISION"));
    }

    [Fact]
    public void Credits_switch_off_keeps_them()
    {
        Assert.Null(new SubtitleAdRules("", credits: false).Match("Subtitles by someone"));
    }

    [Theory]
    [InlineData("you")]
    [InlineData("/.*/")]
    [InlineData("/(a+)+/")]
    [InlineData("/[unclosed/")]
    [InlineData("okay")]
    public void Entries_that_would_cut_dialogue_or_hang_are_skipped(string entry)
    {
        var rules = new SubtitleAdRules(entry, credits: false);
        Assert.Single(rules.Problems);
        Assert.Null(rules.Match("Okay, I love you. Thank you."));
    }

    [Fact]
    public void Whole_cue_goes_and_the_rest_are_numbered_again()
    {
        var srt = Srt("Support us and become VIP member\nto remove all ads from www.OpenSubtitles.org", "Hello.", "<i>Synced by someone</i>", "Goodbye.");
        var result = SrtAdCleaner.Clean(srt, Default);

        Assert.Equal(4, result.Cues);
        Assert.Equal(new[] { 1, 3 }, result.Removed.Select(r => r.Number));
        Assert.Equal("1\r\n00:00:01,000 --> 00:00:01,900\r\nHello.\r\n\r\n2\r\n00:00:03,000 --> 00:00:03,900\r\nGoodbye.\r\n\r\n", result.Text);
        Assert.Equal("00:00:00,000", result.Removed[0].Time);
        Assert.Contains("VIP member / to remove all ads", result.Removed[0].Text);
    }

    [Fact]
    public void A_file_without_ads_is_left_exactly_as_it_was()
    {
        var srt = "1\n00:00:01,000 --> 00:00:02,000\nHello.\n\n\n2\n00:00:03,000 --> 00:00:04,000\nBye.";
        var result = SrtAdCleaner.Clean(srt, Default);
        Assert.Empty(result.Removed);
        Assert.Same(srt, result.Text);
    }

    [Fact]
    public void A_blank_line_inside_a_cue_keeps_the_cue_together()
    {
        var srt = "1\n00:00:01,000 --> 00:00:02,000\nHello.\n\nStill talking.\n\n2\n00:00:03,000 --> 00:00:04,000\nwww.tvsubtitles.net\n";
        var result = SrtAdCleaner.Clean(srt, Default);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\nHello.\n\nStill talking.\n", result.Text);
    }

    [Fact]
    public void Non_utf8_files_keep_every_other_byte()
    {
        // Windows-1252 "café" (é = 0xE9) is not valid UTF-8: read as Latin-1 and written back byte for byte.
        var latin = Encoding.Latin1;
        var bytes = latin.GetBytes(Srt("café", "www.tvsubtitles.net"));
        var file = SrtAdCleaner.Decode(bytes);
        var cleaned = SrtAdCleaner.Clean(file.Text, Default);
        var written = SrtAdCleaner.Encode(file, cleaned.Text);
        Assert.Equal(latin.GetBytes(Srt("café")), written);
    }

    [Fact]
    public void Utf8_byte_order_mark_is_kept()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Srt("naïve", "www.tvsubtitles.net"))).ToArray();
        var file = SrtAdCleaner.Decode(bytes);
        var written = SrtAdCleaner.Encode(file, SrtAdCleaner.Clean(file.Text, Default).Text);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Srt("naïve"))), written);
    }

    [Fact]
    public void Suggestions_need_two_films_or_shows_and_skip_one_shows_own_plug()
    {
        var suggestions = new SubtitleAdSuggestions();
        void Add(string title, params string[] cues) => suggestions.Add(title, SrtAdCleaner.Clean(Srt(cues), Default));

        Add("My Movie (2020)", "Visit somesite.blogspot.com for more", "Hello.", "Bye.", "a", "b", "c", "d");
        Add("Other Movie (2021)", "Hello.", "x", "y", "z", "w", "Bye.", "Get more at somesite.blogspot.com");
        Add("My Show", "Log on to channel.com/myshow.", "Hi.");
        Add("My Show", "Log on to channel.com/myshow.", "Hi.");

        var found = suggestions.Waiting(Default);
        var only = Assert.Single(found);
        Assert.Equal("somesite.blogspot.com", only.Entry);
        Assert.Equal(2, only.Titles);
    }

    [Theory]
    [InlineData(@"\\nas\Shows\My Show\Season 01\My Show - S01E01.en.srt", "My Show")]
    [InlineData(@"\\nas\Movies\My Movie (2020)\My Movie (2020).en.srt", "My Movie (2020)")]
    public void Title_is_the_show_or_the_movie_folder(string path, string title)
    {
        if (Path.DirectorySeparatorChar != '\\') path = path.Replace('\\', '/');
        Assert.Equal(title, SubtitleAdSuggestions.TitleOf(path));
    }

    [Fact]
    public void Files_for_a_video_are_its_own_srt_files_only()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-ads-").FullName;
        try
        {
            foreach (var f in new[] { "My Movie (2020).mkv", "My Movie (2020).en.srt", "My Movie (2020).srt", "My Movie (2020) Extras.srt", "My Movie (2020).en.ass" })
                File.WriteAllText(Path.Combine(dir, f), "x");
            var found = SubtitleAdFiles.FilesFor(Path.Combine(dir, "My Movie (2020).mkv")).Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "My Movie (2020).en.srt", "My Movie (2020).srt" }, found);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Cleaning_a_file_backs_it_up_and_records_it_for_restore()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-ads-").FullName;
        try
        {
            var video = Path.Combine(dir, "My Movie (2020).mkv");
            var srt = Path.Combine(dir, "My Movie (2020).en.srt");
            File.WriteAllText(video, "x");
            File.WriteAllText(srt, Srt("Hello.", "www.tvsubtitles.net"));
            var state = new SubremoverState();
            var files = new SubtitleAdFiles(Default, false, BackupMode.NextToOriginal, null, new NullLogger());
            files.Begin(state);

            Assert.True(files.NeedsCheck(srt, state));
            files.Clean(srt, video, state);

            Assert.Equal(Srt("Hello."), File.ReadAllText(srt));
            Assert.Equal(Srt("Hello.", "www.tvsubtitles.net"), File.ReadAllText(srt + BackupPaths.SideBySideSuffix));
            var backup = Assert.Single(state.Backups);
            Assert.Equal(srt, backup.OriginalPath);
            Assert.True(SubtitleAdFiles.IsSubtitleBackup(backup));
            Assert.Equal("Subtitle file: removed 1 ad line", Assert.Single(backup.Changes));
            Assert.False(files.NeedsCheck(srt, state));
            Assert.Equal(1, files.CuesRemoved);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Counting_only_changes_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("subremover-ads-").FullName;
        try
        {
            var video = Path.Combine(dir, "My Movie (2020).mkv");
            var srt = Path.Combine(dir, "My Movie (2020).en.srt");
            File.WriteAllText(srt, Srt("Hello.", "www.tvsubtitles.net"));
            var state = new SubremoverState();
            var files = new SubtitleAdFiles(Default, true, BackupMode.NextToOriginal, null, new NullLogger());
            files.Clean(srt, video, state);
            Assert.Equal(Srt("Hello.", "www.tvsubtitles.net"), File.ReadAllText(srt));
            Assert.Empty(state.Backups);
            Assert.Empty(state.AdChecked);
            Assert.Equal(1, files.FilesWithAds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Ocr_files_are_left_alone()
    {
        var state = new SubremoverState();
        Assert.True(SubtitleAdFiles.IsOcrFile(@"x\My Movie (2020).pgs.eng.srt", @"x\My Movie (2020).mkv", state));
        Assert.False(SubtitleAdFiles.IsOcrFile(@"x\My Movie (2020).eng.srt", @"x\My Movie (2020).mkv", state));
    }

    [Fact]
    public void Default_list_has_no_problems() => Assert.Empty(Default.Problems);
}
