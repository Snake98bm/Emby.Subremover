using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover.ScheduledTasks
{
    /// <summary>
    /// "Remove ads from subtitle files": every .srt file next to the videos in the selected libraries (and path filter).
    /// Find only lists what it would take out and suggests more for the list; Remove takes it out, backing each file up.
    /// Changes no video. Never runs next to a run or a restore: both change the record of backups.
    /// </summary>
    public class SubtitleAdsTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _embyLog;
        private readonly string _logDirectory;
        private readonly StateStore _store;

        public SubtitleAdsTask(ILibraryManager libraryManager, ILogManager logManager, IJsonSerializer json, IApplicationPaths appPaths)
        {
            _libraryManager = libraryManager;
            _embyLog = logManager.GetLogger("SubRemover");
            _logDirectory = appPaths.LogDirectoryPath;
            _store = new StateStore(appPaths.PluginConfigurationsPath, json);
            LastResults.UseFolder(appPaths.PluginConfigurationsPath);
        }

        public string Name => "Remove ads from subtitle files";
        public string Key => "SubremoverSubtitleAds";
        public string Description =>
            "Takes ad lines out of the .srt files next to your videos, backing each one up. Changes no video. In test mode it only " +
            "lists them. Configure under Plugins → SubRemover → Subtitle files.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        /// <summary>When the last run finished and what it found, for the settings page; kept across restarts.</summary>
        internal static string LastResult
        {
            get => _lastResult ?? LastResults.Load("ads");
            private set { _lastResult = value; LastResults.Save("ads", value); }
        }

        private static string _lastResult;

        /// <summary>The log file of the last Find (test run) or Remove, for the link on the settings page.</summary>
        internal static string LastLogName { get; set; }

        /// <summary>What the last Find or Remove suggests adding to the list, for the settings page.</summary>
        internal static List<SubtitleAdSuggestion> LastSuggestions { get; private set; } = new List<SubtitleAdSuggestion>();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var options = Plugin.Instance.Options;
            var countOnly = Plugin.Instance.TakeAdRunMode() ?? !options.ChangesSubtitleFiles;
            using (var log = new RunLog(_logDirectory, "subremover", _embyLog))
            {
                LastLogName = log.FileName;
                var version = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                log.Summary("SubRemover {0}: {1} ads in subtitle files. Details: {2}", version,
                    countOnly ? "finding (nothing is changed)" : "removing", log.FileName);
                if (!TaskGate.TryEnter())
                {
                    LastResult = "Didn't run: another SubRemover task is going. Try again when it has finished.";
                    log.Summary("SubRemover: {0}", LastResult);
                    return;
                }
                try
                {
                    LastResult = await Task.Run(() => Run(log, options, countOnly, cancellationToken, progress), cancellationToken).ConfigureAwait(false);
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

        private string Run(ILogger log, PluginOptions options, bool countOnly, CancellationToken ct, IProgress<double> progress)
        {
            if (string.IsNullOrWhiteSpace(options.Libraries)) return "Didn't run: choose libraries first (Run tab).";
            var rules = options.AdRules();
            foreach (var problem in rules.Problems) log.Warn($"SubRemover: 'Text to remove': {problem}");

            var videos = RemoveSubtitlesTask.FindVideos(_libraryManager, options, null, log);
            var state = _store.Load();
            var files = new SubtitleAdFiles(rules, countOnly, options.BackupMode, options.BackupFolder, log);
            files.Begin(state);
            log.Info($"SubRemover: {videos.Count} videos; looking at their .srt files");

            var position = 0;
            var lastSaved = DateTime.UtcNow;
            var atOnce = Math.Max(1, Math.Min(8, options.VideosAtOnce));
            // Each .srt file is cleaned once, by one thread: "Movie.mkv" and "Movie.mp4" share their files.
            var claimed = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Parallel.ForEach(videos, new ParallelOptions { MaxDegreeOfParallelism = atOnce, CancellationToken = ct }, video =>
                {
                    progress.Report(100.0 * Interlocked.Increment(ref position) / Math.Max(1, videos.Count));
                    List<string> srts;
                    try { srts = SubtitleAdFiles.FilesFor(video.Path); }
                    catch (Exception ex) { log.Debug($"SubRemover: couldn't list the subtitle files of {video.Path}: {ex.Message}"); return; }
                    // The buttons look at every file again; only runs skip the ones checked before.
                    foreach (var srt in srts)
                    {
                        if (claimed.TryAdd(srt, true)) files.Clean(srt, video.Path, state);
                    }

                    if (!countOnly && DateTime.UtcNow - lastSaved > TimeSpan.FromSeconds(30))
                    {
                        lock (state)
                        {
                            _store.Save(state);
                            lastSaved = DateTime.UtcNow;
                        }
                    }
                });
            }
            finally
            {
                // Also when stopped: the files already changed must be on the restore list.
                if (!countOnly) lock (state) _store.Save(state);
            }
            progress.Report(100);

            var suggestions = files.Suggestions.Waiting(rules);
            LastSuggestions = suggestions;
            if (suggestions.Count > 0)
            {
                log.Info("SubRemover: text in subtitle files the 'Text to remove' list misses (in 2+ films or shows); add it with " +
                         "'Suggested text' on the Subtitle files tab:" +
                         string.Concat(suggestions.Select(s => $"\n    {s.Entry} ({s.Titles}): e.g. \"{s.Example}\" in " +
                                                              string.Join(", ", s.TitleNames.Take(5)) + (s.TitleNames.Count > 5 ? $" and {s.TitleNames.Count - 5} more" : ""))));
            }

            return $"{(countOnly ? "Find" : "Remove")} finished {DateTime.Now:d MMM HH:mm}: {files.Describe()}." +
                   (suggestions.Count > 0 ? $" {suggestions.Count} suggestion{(suggestions.Count == 1 ? "" : "s")} for the list below." : "");
        }
    }
}
