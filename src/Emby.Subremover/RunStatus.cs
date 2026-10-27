using System;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover
{
    /// <summary>The status line under the Run now button.</summary>
    public static class RunStatus
    {
        private const string Caption = "Status";

        public static StatusItem Started(DateTime nowLocal) =>
            new StatusItem(Caption, $"Started at {nowLocal:HH:mm}. Reopen this page to see progress and the result.", ItemStatus.InProgress);

        public static StatusItem Describe(TaskState? state, double? progress, TaskResult lastTaskResult, RunSummary lastRun)
        {
            if (state == TaskState.Running)
                return new StatusItem(Caption, progress.HasValue ? $"Running – {Math.Round(progress.Value)}% done" : "Running", ItemStatus.InProgress);

            if (state == TaskState.Cancelling)
                return new StatusItem(Caption, "Stopping…", ItemStatus.Warning);

            // Emby records cancelled and crashed runs; our own summary is only written when a run finishes normally.
            if (lastTaskResult != null && lastTaskResult.Status != TaskCompletionStatus.Completed
                && (lastRun == null || lastTaskResult.EndTimeUtc.UtcDateTime > lastRun.FinishedUtc.AddSeconds(5)))
            {
                var what = lastTaskResult.Status == TaskCompletionStatus.Cancelled ? "was cancelled" : "failed";
                var error = string.IsNullOrEmpty(lastTaskResult.ErrorMessage) ? "" : ": " + lastTaskResult.ErrorMessage;
                return new StatusItem(Caption, $"Last run {what} at {When(lastTaskResult.EndTimeUtc.UtcDateTime)}{error}", ItemStatus.Failed);
            }

            if (lastRun == null)
                return new StatusItem(Caption, "Not run yet", ItemStatus.None);

            if (lastRun.Problem != null)
                return new StatusItem(Caption, $"Didn't run at {When(lastRun.FinishedUtc)}: {lastRun.Problem}", ItemStatus.Failed);

            var counts = lastRun.DryRun
                ? $"{lastRun.WouldChange} would change"
                : $"{lastRun.Changed} changed";
            var text = $"Last run {When(lastRun.FinishedUtc)}{(lastRun.DryRun ? " (test mode)" : "")}: {counts}, " +
                       $"{lastRun.NothingToDo + lastRun.AlreadyDone} already fine, {lastRun.Failed} failed. Log: {lastRun.LogFile}";
            return new StatusItem(Caption, text, lastRun.Failed > 0 ? ItemStatus.Warning : ItemStatus.Succeeded);
        }

        private static string When(DateTime utc) => utc.ToLocalTime().ToString("d MMM HH:mm");
    }
}
