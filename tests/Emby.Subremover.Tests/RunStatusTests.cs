using Emby.Subremover.Core;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Emby.Subremover.Tests;

public class RunStatusTests
{
    private static readonly DateTime Finished = new DateTime(2026, 10, 27, 17, 5, 0, DateTimeKind.Utc);

    private static RunSummary Summary(bool dryRun = false, int failed = 0) => new()
    {
        FinishedUtc = Finished, DryRun = dryRun, Changed = 3, WouldChange = 4, NothingToDo = 10, AlreadyDone = 2, Failed = failed,
        LogFile = "subremover-20261027-100500.txt"
    };

    [Fact]
    public void Never_run()
    {
        var status = RunStatus.Describe(TaskState.Idle, null, null, null);
        Assert.Equal("Not run yet", status.StatusText);
        Assert.Equal(ItemStatus.None, status.Status);
    }

    [Fact]
    public void Running_shows_progress()
    {
        var status = RunStatus.Describe(TaskState.Running, 41.6, null, Summary());
        Assert.Equal("Running – 42% done", status.StatusText);
        Assert.Equal(ItemStatus.InProgress, status.Status);
    }

    [Fact]
    public void Finished_run_shows_counts_and_log_file()
    {
        var status = RunStatus.Describe(TaskState.Idle, null, null, Summary());
        Assert.Contains("3 changed, 12 already fine, 0 failed", status.StatusText);
        Assert.Contains("subremover-20261027-100500.txt", status.StatusText);
        Assert.Equal(ItemStatus.Succeeded, status.Status);
    }

    [Fact]
    public void Test_mode_run_says_so_and_failures_warn()
    {
        var status = RunStatus.Describe(TaskState.Idle, null, null, Summary(dryRun: true, failed: 2));
        Assert.Contains("(test mode): 4 would change", status.StatusText);
        Assert.Contains("2 failed", status.StatusText);
        Assert.Equal(ItemStatus.Warning, status.Status);
    }

    [Fact]
    public void Run_that_could_not_start_shows_why()
    {
        var summary = new RunSummary { FinishedUtc = Finished, Problem = "no libraries selected" };
        var status = RunStatus.Describe(TaskState.Idle, null, null, summary);
        Assert.Contains("Didn't run", status.StatusText);
        Assert.EndsWith("no libraries selected", status.StatusText);
        Assert.Equal(ItemStatus.Failed, status.Status);
    }

    [Fact]
    public void Cancelled_run_newer_than_the_last_summary_is_reported()
    {
        var cancelled = new TaskResult { Status = TaskCompletionStatus.Cancelled, EndTimeUtc = new DateTimeOffset(Finished.AddHours(1)) };
        var status = RunStatus.Describe(TaskState.Idle, null, cancelled, Summary());
        Assert.Contains("was cancelled", status.StatusText);

        var older = new TaskResult { Status = TaskCompletionStatus.Cancelled, EndTimeUtc = new DateTimeOffset(Finished.AddHours(-1)) };
        Assert.Contains("3 changed", RunStatus.Describe(TaskState.Idle, null, older, Summary()).StatusText);
    }

    [Fact]
    public void Status_line_sits_right_under_the_run_now_and_save_buttons()
    {
        var order = typeof(PluginOptions).GetProperties().Select(p => p.Name).ToList();
        Assert.Equal(order.IndexOf(nameof(PluginOptions.RunNowButton)) + 1, order.IndexOf(nameof(PluginOptions.SaveButton)));
        Assert.Equal(order.IndexOf(nameof(PluginOptions.SaveButton)) + 1, order.IndexOf(nameof(PluginOptions.RunStatus)));
    }
}
