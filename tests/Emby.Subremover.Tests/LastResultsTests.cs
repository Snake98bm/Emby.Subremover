using Emby.Subremover.Core;
using Xunit;

namespace Emby.Subremover.Tests;

public class LastResultsTests
{
    [Fact]
    public void Each_tasks_last_result_survives_a_restart()
    {
        var folder = Directory.CreateTempSubdirectory("subremover-last-").FullName;
        try
        {
            LastResults.UseFolder(folder);
            LastResults.Save("hash", "Count finished 16 Nov 05:30: 8 of 3026 looked up have a perfect match");
            LastResults.Save("ads", "Find finished 16 Nov 06:00:\n3 files with ads.");
            LastResults.Save("hash", "Download finished 16 Nov 09:40: 5 subtitle files downloaded");

            // What a restarted Emby reads back: the newest for each task, one line each.
            Assert.Equal("Download finished 16 Nov 09:40: 5 subtitle files downloaded", LastResults.Load("hash"));
            Assert.Equal("Find finished 16 Nov 06:00: 3 files with ads.", LastResults.Load("ads"));
            Assert.Null(LastResults.Load("timing"));
            Assert.Equal(2, File.ReadAllLines(Path.Combine(folder, LastResults.FileName)).Length);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_running_task_also_shows_its_last_result()
    {
        Assert.Equal("Running – 19% done. Last time: Count finished 16 Nov 05:30: 8 found",
            Plugin.WithLast("Running – 19% done", "Count finished 16 Nov 05:30: 8 found"));
        Assert.Equal("Running", Plugin.WithLast("Running", null));
    }
}
