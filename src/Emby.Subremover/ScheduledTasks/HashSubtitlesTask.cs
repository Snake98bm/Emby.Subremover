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
        }

        public string Name => "Download hash-matched subtitles";
        public string Key => "SubremoverHashSubtitles";
        public string Description =>
            "Gets perfect-match (hash) subtitles for videos with no built-in text subtitle, before anything changes them. " +
            "Changes no video. In test mode it only counts them. Configure under Plugins → SubRemover.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        /// <summary>What the last run found, for the settings page (until Emby restarts).</summary>
        internal static string LastResult { get; private set; }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            // The page's two buttons say which; a scheduled run follows Test mode.
            var countOnly = Plugin.Instance.TakeHashRunMode() ?? options.DryRun;
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

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var atOnce = Math.Max(1, Math.Min(8, options.VideosAtOnce));
                var running = new List<Task>();
                foreach (var video in videos)
                {
                    if (stop.IsCancellationRequested) break;
                    progress.Report(100.0 * position++ / Math.Max(1, videos.Count));
                    if (!countOnly && done.ContainsKey(video.Path)) { checkedBefore++; continue; }
                    if (backedUp.Contains(video.Path) || System.IO.File.Exists(video.Path + ".audioremover.bak")) { changedAlready++; continue; }

                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var probe = await Probe(video.Path, log, stop.Token).ConfigureAwait(false);
                            if (probe == null) { Interlocked.Increment(ref failed); return; }
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
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            log.ErrorException("SubRemover: unexpected error on {0}", ex, video.Path);
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
    }
}
