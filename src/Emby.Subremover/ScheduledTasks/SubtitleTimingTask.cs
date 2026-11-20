using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover.ScheduledTasks
{
    /// <summary>
    /// "Fix subtitle file timing": every .srt file next to the videos in the selected libraries (and path filter), measured
    /// against the video's speech. Scan run only logs what it would do and saves it as a plan (Subremover.timing-plan.json);
    /// Fix moves files that are off by the same amount all the way through and deletes out-of-sync copies of a language that
    /// has an in-sync one, backing each up. Fix uses the saved plan for videos whose files haven't changed since the scan,
    /// and measures only the rest. Changes no video. Never runs next to a run, a restore or the ads task: they all change the record of backups.
    /// </summary>
    public class SubtitleTimingTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _embyLog;
        private readonly string _logDirectory;
        private readonly StateStore _store;
        private readonly FfmpegRunner _runner;
        private readonly TaskProgressStore _progress;
        private readonly TimingPlanStore _plans;

        public SubtitleTimingTask(ILibraryManager libraryManager, ILogManager logManager, IFfmpegManager ffmpegManager, IJsonSerializer json, IApplicationPaths appPaths)
        {
            _progress = new TaskProgressStore(appPaths.PluginConfigurationsPath, json);
            _plans = new TimingPlanStore(appPaths.PluginConfigurationsPath, json);
            _libraryManager = libraryManager;
            _embyLog = logManager.GetLogger("SubRemover");
            _logDirectory = appPaths.LogDirectoryPath;
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
            _runner = FfmpegRunner.FromEmby(ffmpegManager);
            LastResults.UseFolder(appPaths.PluginConfigurationsPath);
        }

        public string Name => "Fix subtitle file timing";
        public string Key => "SubremoverSubtitleTiming";
        public string Description =>
            "Checks the .srt files next to your videos against the speech and moves the ones that are off (never stretches " +
            "them); deletes an out-of-sync copy when another in the same language is in sync. Backs each one up. Changes no " +
            "video. Configure under Plugins → SubRemover → Subtitle files.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        /// <summary>When the last run finished and what it found, for the settings page; kept across restarts.</summary>
        internal static string LastResult
        {
            get => _lastResult ?? LastResults.Load("timing");
            private set { _lastResult = value; LastResults.Save("timing", value); }
        }

        private static string _lastResult;
        internal static string LastLogName { get; set; }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            var countOnly = Plugin.Instance.TakeTimingRunMode() ?? !options.ChangesSubtitleFiles;
            // "Start over": forget where a stopped run got to and measure every file again, even ones found good before.
            var startOver = Plugin.Instance.TakeTimingStartOver();
            using (var log = new RunLog(_logDirectory, "subremover", _embyLog))
            {
                LastLogName = log.FileName;
                var version = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                log.Summary("SubRemover {0}: subtitle file timing, {1}. Details: {2}", version,
                    countOnly ? "scan run (nothing is changed)" : "fixing", log.FileName);
                if (!TaskGate.TryEnter())
                {
                    LastResult = "Didn't run: another SubRemover task is going. Try again when it has finished.";
                    log.Summary("SubRemover: {0}", LastResult);
                    return;
                }
                try
                {
                    LastResult = await Run(log, options, countOnly, startOver, cancellationToken, progress).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LastResult = $"Stopped at {DateTime.Now:d MMM HH:mm}. Log: {log.FileName}";
                    throw;
                }
                finally
                {
                    TaskGate.Exit();
                }
                log.Summary("SubRemover: {0}", LastResult);
            }
        }

        private async Task<string> Run(ILogger log, PluginOptions options, bool countOnly, bool startOver, CancellationToken ct, IProgress<double> progress)
        {
            if (string.IsNullOrWhiteSpace(options.Libraries)) return "Didn't run: choose libraries first (Run tab).";
            // Only now, with the gate held and the run going ahead: a refused run keeps the stopped run's place.
            if (startOver) _progress.Delete(ProgressName(countOnly));
            var videos = RemoveSubtitlesTask.FindVideos(_libraryManager, options, null, log);
            var state = _store.Load();
            var files = new SubtitleTimingFiles(_runner, countOnly, options.DeleteOutOfSyncDuplicates, options.DeleteOtherVersionSubtitles, options.BackupMode, options.BackupFolder, log, options.DeleteWayOffSubtitles);
            files.SkipExtracted = options.SkipExtractedTimingCopies;
            files.UntaggedAudioIsEnglish = options.WayOffUntaggedAudioIsEnglish;
            files.Begin(state);
            // Scan run: saves what it finds for each video. Fix: uses that for videos unchanged since (Start over: measures all).
            TimingPlan plan = null;
            if (countOnly || !startOver)
            {
                plan = _plans.LoadFor(files.PlanSignature);
                files.Plan = plan;
            }
            var planned = plan?.VideoCount() ?? 0;
            log.Info($"SubRemover: {videos.Count} videos; measuring the timing of their .srt files" +
                     (startOver ? " (starting over: every file is measured again)" : countOnly ? "" : " not measured before, or changed since") +
                     (!countOnly && planned > 0 ? $"; {planned} videos are done from the scan run of {plan.SavedUtc.ToLocalTime():d MMM HH:mm} unless they changed since" : ""));

            // Carry on where a stopped run with the same settings got to.
            var task = ProgressName(countOnly);
            var key = ProgressKey(options);
            var saved = _progress.LoadFor(task, key);
            var skip = new HashSet<string>(saved?.Done ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (saved != null)
            {
                log.Info($"SubRemover: carrying on the {(countOnly ? "scan run" : "fix")} started {saved.StartedUtc.ToLocalTime():d MMM HH:mm} " +
                         $"({saved.Describe()}); its totals so far are included. Tick Start over on the settings page to begin again.");
                files.VideosChecked = saved.Count("videos"); files.FilesChecked = saved.Count("files"); files.Shifted = saved.Count("shifted");
                files.Deleted = saved.Count("deleted"); files.LeftAlone = saved.Count("leftAlone"); files.Failed = saved.Count("failed");
                files.FromPlan = saved.Count("fromPlan"); files.FilesFromPlan = saved.Count("filesFromPlan"); files.Rescanned = saved.Count("rescanned");
                files.WayOffDeleted = saved.Count("wayOffDeleted"); files.SkippedCopies = saved.Count("skippedCopies");
            }
            else saved = new TaskProgress { Key = key, StartedUtc = DateTime.UtcNow, LogFile = (log as RunLog)?.FileName };
            saved.Total = videos.Count;
            void SaveProgress()
            {
                lock (saved)
                {
                    saved.Counts = new Dictionary<string, int>
                    {
                        ["videos"] = files.VideosChecked, ["files"] = files.FilesChecked, ["shifted"] = files.Shifted,
                        ["deleted"] = files.Deleted, ["leftAlone"] = files.LeftAlone, ["failed"] = files.Failed,
                        ["fromPlan"] = files.FromPlan, ["filesFromPlan"] = files.FilesFromPlan, ["rescanned"] = files.Rescanned,
                        ["wayOffDeleted"] = files.WayOffDeleted, ["skippedCopies"] = files.SkippedCopies
                    };
                    _progress.Save(task, saved);
                    if (plan != null) _plans.Save(plan);
                }
            }

            // "Videos per timing run": measure this many, then stop; the next click carries on.
            var limit = Math.Max(0, options.TimingVideosPerRun);
            var measuredBefore = files.VideosChecked;
            var limitReached = false;

            var position = 0;
            var lastSaved = DateTime.UtcNow;
            // Each video's audio is decoded in full: a few at once, at low priority.
            using (var slots = new SemaphoreSlim(Math.Max(1, Math.Min(8, options.TimingVideosAtOnce))))
            {
                var running = new List<Task>();
                foreach (var video in videos)
                {
                    if (skip.Contains(video.Path))
                    {
                        progress.Report(100.0 * Interlocked.Increment(ref position) / Math.Max(1, videos.Count));
                        continue;
                    }
                    // Stopped: dispatch nothing more, but let the videos being measured finish (below) before saving.
                    try { await slots.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    running.RemoveAll(t => t.IsCompleted);
                    if (limit > 0)
                    {
                        // Videos still being measured may count towards the limit: wait for them rather than overshoot.
                        while (files.VideosChecked - measuredBefore < limit && files.VideosChecked - measuredBefore + running.Count >= limit)
                        {
                            await Task.WhenAny(running).ConfigureAwait(false);
                            running.RemoveAll(t => t.IsCompleted);
                        }
                        if (files.VideosChecked - measuredBefore >= limit)
                        {
                            slots.Release();
                            limitReached = true;
                            break;
                        }
                    }
                    if (ct.IsCancellationRequested)
                    {
                        slots.Release();
                        break;
                    }
                    running.Add(Task.Run(async () =>
                    {
                        var finished = false;
                        try
                        {
                            // Fixing skips files measured before and unchanged since (they were good, or were fixed);
                            // a scan run and Start over measure them all.
                            await files.Check(video.Path, state, !countOnly && !startOver, ct).ConfigureAwait(false);
                            finished = true;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref files.Failed);
                            log.Error($"SubRemover: couldn't check the subtitle timing of {video.Path}: {ex.Message}");
                            finished = true;
                        }
                        finally
                        {
                            if (finished) lock (saved) saved.Done.Add(video.Path);
                            progress.Report(100.0 * Interlocked.Increment(ref position) / Math.Max(1, videos.Count));
                            if (DateTime.UtcNow - lastSaved > TimeSpan.FromSeconds(30))
                            {
                                lock (state)
                                {
                                    if (!countOnly) _store.Save(state);
                                    SaveProgress();
                                    lastSaved = DateTime.UtcNow;
                                }
                            }
                            slots.Release();
                        }
                    }));
                }
                // Also when stopped: every worker has finished (and released its slot) before anything is saved or disposed.
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            if (!countOnly) lock (state) _store.Save(state);
            // A whole scan: forget videos that are gone or no longer in the libraries. Not with a path filter: the videos
            // outside it are still there, and their saved measurements still count.
            if (plan != null && countOnly && !limitReached && !ct.IsCancellationRequested && string.IsNullOrWhiteSpace(options.OnlyPathContains))
                plan.KeepOnly(videos.Select(v => v.Path));
            if (plan != null) _plans.Save(plan);
            // Stopped by hand, or at the per-run limit: keep where it got to for the next click.
            if (ct.IsCancellationRequested || limitReached) SaveProgress();
            else _progress.Delete(task);
            ct.ThrowIfCancellationRequested();
            progress.Report(100);
            var what = countOnly ? "Scan run" : "Fix";
            if (limitReached)
                return $"{what} paused {DateTime.Now:d MMM HH:mm} after measuring {files.VideosChecked - measuredBefore} videos (Videos per timing run is {limit}; {saved.Done.Count:N0} of {saved.Total:N0} done so far): " +
                       $"{files.Describe()}. Click {(countOnly ? "Scan run" : "Fix timing now")} again to carry on.";
            return $"{what} finished {DateTime.Now:d MMM HH:mm}: {files.Describe()}." +
                   (countOnly && plan != null ? " Saved as the plan for Fix timing now, which only measures again what changed since." : "");
        }

        internal static string ProgressName(bool countOnly) => countOnly ? "timing-test" : "timing-fix";

        // A stopped run only carries on with the same libraries, path filter and delete settings.
        internal static string ProgressKey(PluginOptions options) =>
            string.Join("|", options.Libraries, options.OnlyPathContains, options.DeleteOutOfSyncDuplicates, options.DeleteOtherVersionSubtitles,
                options.DeleteWayOffSubtitles, options.WayOffUntaggedAudioIsEnglish, options.SkipExtractedTimingCopies);

        /// <summary>Where a stopped scan run (true) or fix (false) got to, for the settings page; null when there's nothing to carry on.</summary>
        internal static TaskProgress Stopped(TaskProgressStore store, PluginOptions options, bool countOnly)
        {
            var saved = store.Load(ProgressName(countOnly));
            return saved != null && saved.Key == ProgressKey(options) ? saved : null;
        }
    }
}
