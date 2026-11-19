using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

// The same test is in AudioRemover: both plugins write the shared hash file, so each must keep the other's counts.
public class HashStoreRejectedTests
{
    [Fact]
    public void Rejected_downloads_are_kept_and_count_toward_the_daily_limit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "subremover-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var today = DateTime.UtcNow;
            var store = new OriginalHashStore(folder, new TestJsonSerializer());
            store.Add(@"C:\Media\My Movie (2020).mkv", new OriginalHash { CheckedUtc = today, Downloaded = { "a.eng.srt" }, Rejected = 2 });
            // Another write (as the other plugin would make) keeps the first record's count.
            store.Add(@"C:\Media\My Show S01E01.mkv", new OriginalHash { CheckedUtc = today });

            var again = new OriginalHashStore(folder, new TestJsonSerializer());
            Assert.Equal(2, again.Load()[@"C:\Media\My Movie (2020).mkv"].Rejected);
            Assert.Equal(3, again.DownloadsOn(today));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
