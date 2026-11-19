using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover.ScheduledTasks
{
    /// <summary>
    /// "Download hash-matched subtitles": goes through the selected libraries without changing any video and, for each
    /// video still as it was (no built-in text subtitle in a kept language), asks Emby's subtitle downloaders for perfect
    /// (hash) matches. Count only: asks, downloads nothing. Download: saves them and each video's original hash, so the
    /// edits that follow don't lose the match. Stops at the first refusal (usually the provider's daily limit); the next
    /// run carries on with the videos not done yet.
    /// </summary>
    public class HashSubtitlesTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _embyLog;
        private readonly string _logDirectory;
        private readonly StateStore _store;
        private readonly OriginalHashStore _hashes;
        private readonly ISubtitleManager _subtitles;
        private readonly FfmpegRunner _runner;
        private readonly IJsonSerializer _json;
        private readonly TaskProgressStore _progress;

        public HashSubtitlesTask(ILibraryManager libraryManager, ILogManager logManager, IFfmpegManager ffmpegManager,
            IJsonSerializer json, IApplicationPaths appPaths, ISubtitleManager subtitleManager)
        {
            _libraryManager = libraryManager;
            _embyLog = logManager.GetLogger("SubRemover");
            _logDirectory = appPaths.LogDirectoryPath;
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
            _hashes = new OriginalHashStore(appPaths.PluginConfigurationsPath, json);
            _subtitles = subtitleManager;
            _runner = FfmpegRunner.FromEmby(ffmpegManager);
            _json = json;
            _progress = new TaskProgressStore(appPaths.PluginConfigurationsPath, json);
            LastResults.UseFolder(appPaths.PluginConfigurationsPath);
        }

        public string Name => "Download hash-matched subtitles";
        public string Key => "SubremoverHashSubtitles";
        public string Description =>
            "Gets perfect-match (hash) subtitles for videos with no built-in text subtitle, before anything changes them. " +
            "Changes no video. In test mode it only counts them. Configure under Plugins → SubRemover.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        /// <summary>When the last run finished and what it found, for the settings page; kept across restarts.</summary>
        internal static string LastResult
        {
            get => _lastResult ?? LastResults.Load("hash");
            private set { _lastResult = value; LastResults.Save("hash", value); }
        }

        private static string _lastResult;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            // The page's two buttons say which; a scheduled run follows Test mode (and its subtitle-files exception).
            var countOnly = Plugin.Instance.TakeHashRunMode() ?? !options.ChangesSubtitleFiles;
            // "Start over" throws away where a stopped run got to; otherwise (a button or the schedule) it carries on.
            if (Plugin.Instance.TakeHashStartOver()) _progress.Delete(ProgressName(countOnly));
            using (var log = new RunLog(_logDirectory, "subremover", _embyLog))
            {
                var version = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                log.Summary("SubRemover {0}: {1} hash-matched subtitles. Details: {2}", version,
                    countOnly ? "counting (nothing is downloaded)" : "downloading", log.FileName);
                try
                {
                    LastResult = await Run(log, options, countOnly, cancellationToken, progress).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LastResult = $"Stopped at {DateTime.Now:d MMM HH:mm}. Log: {log.FileName}";
                    throw;
                }
                log.Summary("SubRemover: {0}", LastResult);
            }
        }

        private async Task<string> Run(ILogger log, PluginOptions options, bool countOnly, CancellationToken ct, IProgress<double> progress)
        {
            var keep = PlannerSettings.ParseLanguages(options.KeepLanguages).ToList();
            if (string.IsNullOrWhiteSpace(options.Libraries) || keep.Count == 0)
                return "Didn't run: choose libraries and subtitle languages to keep first.";

            var videos = RemoveSubtitlesTask.FindVideos(_libraryManager, options, null, log);
            var done = _hashes.Load();
            HashSet<string> backedUp;
            {
                var state = _store.Load();
                backedUp = new HashSet<string>(state.Backups.Select(b => b.OriginalPath), StringComparer.OrdinalIgnoreCase);
            }
            log.Info($"SubRemover: {videos.Count} videos; looking up kept languages {string.Join(", ", keep)}");

            var lookups = new HashMatchedSubtitles(_subtitles, _hashes, log, "SubRemover");
            var mode = countOnly ? HashMode.CountOnly : HashMode.Download;
            int checkedBefore = 0, changedAlready = 0, builtIn = 0, haveFiles = 0, searched = 0, matched = 0, files = 0, failed = 0, position = 0;
            string stoppedAt = null;
            var limitReached = false;

            // Carry on where a stopped run with the same settings got to.
            var task = ProgressName(countOnly);
            var key = ProgressKey(options);
            var saved = _progress.LoadFor(task, key);
            var skip = new HashSet<string>(saved?.Done ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            if (saved != null)
            {
                log.Info($"SubRemover: carrying on the {(countOnly ? "count" : "download")} started {saved.StartedUtc.ToLocalTime():d MMM HH:mm} " +
                         $"({saved.Describe()}); its totals so far are included. Start over on the settings page to begin again.");
                checkedBefore = saved.Count("checkedBefore"); changedAlready = saved.Count("changedAlready"); builtIn = saved.Count("builtIn");
                haveFiles = saved.Count("haveFiles"); searched = saved.Count("searched"); matched = saved.Count("matched");
                files = saved.Count("files"); failed = saved.Count("failed");
            }
            else saved = new TaskProgress { Key = key, StartedUtc = DateTime.UtcNow, LogFile = (log as RunLog)?.FileName };
            saved.Total = videos.Count;
            var lastSaved = DateTime.UtcNow;
            void SaveProgress()
            {
                lock (saved)
                {
                    saved.Counts = new Dictionary<string, int>
                    {
                        ["checkedBefore"] = checkedBefore, ["changedAlready"] = changedAlready, ["builtIn"] = builtIn, ["haveFiles"] = haveFiles,
                        ["searched"] = searched, ["matched"] = matched, ["files"] = files, ["failed"] = failed
                    };
                    _progress.Save(task, saved);
                }
                lastSaved = DateTime.UtcNow;
            }
            void MarkDone(string path) { lock (saved) saved.Done.Add(path); }

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var atOnce = Math.Max(1, Math.Min(8, options.VideosAtOnce));
                var running = new List<Task>();
                foreach (var video in videos)
                {
                    if (stop.IsCancellationRequested) break;
                    progress.Report(100.0 * position++ / Math.Max(1, videos.Count));
                    if (DateTime.UtcNow - lastSaved > TimeSpan.FromSeconds(30)) SaveProgress();
                    if (skip.Contains(video.Path)) continue;
                    if (!countOnly && done.ContainsKey(video.Path)) { checkedBefore++; MarkDone(video.Path); continue; }
                    if (backedUp.Contains(video.Path) || System.IO.File.Exists(video.Path + ".audioremover.bak")) { changedAlready++; MarkDone(video.Path); continue; }

                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var probe = await Probe(video.Path, log, stop.Token).ConfigureAwait(false);
                            if (probe == null) { Interlocked.Increment(ref failed); MarkDone(video.Path); return; }
                            var text = probe.streams.Where(s => s.IsSubtitle && SubtitlePlanner.IsTextCodec(s.codec_name)).Select(s => s.Language).ToList();
                            var languages = HashMatchedSubtitles.LanguagesToSearch(keep, text, ExistingSubtitles.Find(video.Path, new string[0]).Keys);
                            if (HashMatchedSubtitles.LanguagesToSearch(keep, text, new string[0]).Count == 0) Interlocked.Increment(ref builtIn);
                            else if (languages.Count == 0) Interlocked.Increment(ref haveFiles);
                            else Interlocked.Increment(ref searched);

                            var result = await lookups.Lookup(video, languages, mode, stop.Token, options.HashDownloadsPerDay).ConfigureAwait(false);
                            if (result.Check == HashCheck.TryLater)
                            {
                                if (result.LimitReached) limitReached = true;
                                else Interlocked.Increment(ref failed);
                                if (Interlocked.CompareExchange(ref stoppedAt, video.Path, null) == null) stop.Cancel();
                                return;
                            }
                            if (result.Found.Count > 0) Interlocked.Increment(ref matched);
                            Interlocked.Add(ref files, result.Downloaded.Count);
                            MarkDone(video.Path);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            log.ErrorException("SubRemover: unexpected error on {0}", ex, video.Path);
                            MarkDone(video.Path);
                        }
                    }));

                    if (running.Count >= atOnce)
                    {
                        await Task.WhenAny(running).ConfigureAwait(false);
                        running.RemoveAll(t => t.IsCompleted);
                    }
                }
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            // Stopped (by hand, the daily limit or a failed search): keep where it got to for the next run.
            if (ct.IsCancellationRequested || stoppedAt != null || limitReached) SaveProgress();
            else _progress.Delete(task);
            ct.ThrowIfCancellationRequested();
            progress.Report(100);

            var found = countOnly
                ? $"{matched} of {searched} looked up have a perfect match (nothing downloaded)"
                : $"{matched} of {searched} looked up had a perfect match; {files} subtitle files downloaded";
            return $"{(countOnly ? "Count" : "Download")} finished {DateTime.Now:d MMM HH:mm}: {found}. " +
                   $"Skipped: {builtIn} with a built-in text subtitle, {haveFiles} with subtitle files already, " +
                   $"{changedAlready} already changed (no match possible)" + (countOnly ? "" : $", {checkedBefore} done before") + "." +
                   (limitReached ? $" Stopped early: today's limit of {options.HashDownloadsPerDay} downloads is used up - run it again tomorrow to carry on." :
                    stoppedAt != null ? $" Stopped early: the search failed at {stoppedAt} (often the provider's daily limit) - run it again later to carry on." : "") +
                   (failed > 0 && stoppedAt == null ? $" {failed} couldn't be read." : "");
        }

        private async Task<ProbeResult> Probe(string path, ILogger log, CancellationToken ct)
        {
            var result = await _runner.RunFfprobe(FfmpegArgs.Probe(path), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                log.Error("SubRemover: ffprobe failed for {0} (exit {1}): {2}", path, result.ExitCode, result.StdErr);
                return null;
            }
            try
            {
                return _json.DeserializeFromString<ProbeResult>(result.StdOut);
            }
            catch (Exception ex)
            {
                log.ErrorException("SubRemover: could not read ffprobe output for {0}", ex, path);
                return null;
            }
        }

        internal static string ProgressName(bool countOnly) => countOnly ? "hash-count" : "hash-download";

        // A stopped run only carries on with the same libraries, path filter and languages.
        internal static string ProgressKey(PluginOptions options) =>
            string.Join("|", options.Libraries, options.OnlyPathContains, options.KeepLanguages);

        /// <summary>Where a stopped Count or Download got to, for the settings page; null when there's nothing to carry on.</summary>
        internal static TaskProgress Stopped(TaskProgressStore store, PluginOptions options, bool countOnly)
        {
            var saved = store.Load(ProgressName(countOnly));
            return saved != null && saved.Key == ProgressKey(options) ? saved : null;
        }
    }
}
