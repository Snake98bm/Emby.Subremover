using System;
using System.IO;
using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class HashMatchedSubtitlesTests
{
    [Fact]
    public void Hash_of_zeros_is_just_the_size()
    {
        Assert.Equal("0000000000020000", OpenSubtitlesHash.Compute(new MemoryStream(new byte[128 * 1024])));
    }

    [Fact]
    public void Hash_adds_the_first_and_last_64_KB_as_8_byte_numbers()
    {
        var bytes = new byte[200 * 1024];
        BitConverter.GetBytes(5UL).CopyTo(bytes, 0);                      // in the first 64 KB
        BitConverter.GetBytes(7UL).CopyTo(bytes, 100 * 1024);             // in neither: not counted
        BitConverter.GetBytes(11UL).CopyTo(bytes, bytes.Length - 8);      // in the last 64 KB
        Assert.Equal((200UL * 1024 + 5 + 11).ToString("x16"), OpenSubtitlesHash.Compute(new MemoryStream(bytes)));
    }

    [Fact]
    public void A_small_file_counts_its_bytes_from_both_ends()
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(3UL).CopyTo(bytes, 0);
        Assert.Equal((16UL + 3 + 3).ToString("x16"), OpenSubtitlesHash.Compute(new MemoryStream(bytes)));
    }

    [Fact]
    public void Searches_kept_languages_with_no_file_when_there_is_no_built_in_text_subtitle()
    {
        var languages = HashMatchedSubtitles.LanguagesToSearch(
            new[] { "eng", "fre", "ger", "und" },
            embeddedTextLanguages: new[] { "spa" },
            fileLanguages: new[] { "fre" });
        Assert.Equal(new[] { "eng", "ger" }, languages);
    }

    [Theory]
    [InlineData("ger")]
    [InlineData("und")]
    [InlineData("")]
    public void A_built_in_text_subtitle_in_a_kept_or_unknown_language_means_no_search(string embedded)
    {
        Assert.Empty(HashMatchedSubtitles.LanguagesToSearch(new[] { "eng", "ger" }, new[] { embedded }, new string[0]));
    }

    [Fact]
    public void Store_keeps_hashes_by_path_ignoring_case()
    {
        var folder = Path.Combine(Path.GetTempPath(), "subremover-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new OriginalHashStore(folder, new TestJsonSerializer());
            const string video = @"\\nas\Emby\Movies\My Movie (2020)\My Movie (2020).mkv";
            Assert.False(store.Has(video));
            store.Add(video, new OriginalHash { Hash = "0000000000020000", Size = 131072, CheckedBy = "AudioRemover" });
            Assert.True(store.Has(video.ToUpperInvariant()));
            Assert.Equal("AudioRemover", new OriginalHashStore(folder, new TestJsonSerializer()).Load()[video].CheckedBy);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class HashDownloadCountTests
{
    [Fact]
    public void Counts_todays_downloads_from_both_plugins()
    {
        var folder = Path.Combine(Path.GetTempPath(), "subremover-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new OriginalHashStore(folder, new TestJsonSerializer());
            var today = DateTime.UtcNow;
            store.Add(@"C:\Media\My Movie (2020).mkv", new OriginalHash { CheckedUtc = today, CheckedBy = "SubRemover", Downloaded = { "a.eng.srt", "a.ger.srt" } });
            store.Add(@"C:\Media\My Show S01E01.mkv", new OriginalHash { CheckedUtc = today, CheckedBy = "AudioRemover", Downloaded = { "b.eng.srt" } });
            store.Add(@"C:\Media\My Show S01E02.mkv", new OriginalHash { CheckedUtc = today.AddDays(-1), Downloaded = { "c.eng.srt" } });
            store.Add(@"C:\Media\My Show S01E03.mkv", new OriginalHash { CheckedUtc = today });
            Assert.Equal(3, store.DownloadsOn(today));
            Assert.Equal(1, store.DownloadsOn(today.AddDays(-1)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Ten_a_day_by_default()
    {
        Assert.Equal(10, new PluginOptions().HashDownloadsPerDay);
    }
}

public class HashMatchedDefaultTests
{
    [Fact]
    public void On_by_default_and_switched_on_once_for_older_saved_settings()
    {
        Assert.True(new PluginOptions().DownloadHashMatchedSubtitles);

        var older = new PluginOptions { DownloadHashMatchedSubtitles = false, OptionsVersion = 1 };
        OptionsStore.Upgrade(older);
        Assert.True(older.DownloadHashMatchedSubtitles);
        Assert.Equal(2, older.OptionsVersion);

        var chosen = new PluginOptions { DownloadHashMatchedSubtitles = false, OptionsVersion = 2 };
        OptionsStore.Upgrade(chosen);
        Assert.False(chosen.DownloadHashMatchedSubtitles);
    }
}
