using System;
using System.Linq;
using System.Threading;
using Emby.Subremover.Core;
using Emby.Subremover.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover
{
    /// <summary>
    /// "Process new videos as they're added": collects the videos Emby adds and, once none have come in for a few
    /// minutes, starts one run for just those. Waiting lets a whole season arrive as one batch with one log, and keeps
    /// SubRemover off files that are still being copied.
    /// </summary>
    public class NewVideoWatcher : IServerEntryPoint
    {
        public static readonly TimeSpan WaitAfterLastVideo = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan WaitWhileBusy = TimeSpan.FromMinutes(2);

        private readonly ILibraryManager _libraryManager;
        private readonly ITaskManager _taskManager;
        private readonly ILogger _log;
        private readonly PendingVideos _waiting = new PendingVideos();
        private readonly object _timerLock = new object();
        private Timer _timer;

        /// <summary>How long after Emby starts an interrupted run is picked up again: Emby is busy starting up before that.</summary>
        public static readonly TimeSpan ResumeAfterStart = TimeSpan.FromMinutes(3);

        private readonly CheckpointStore _checkpoints;
        private Timer _resumeTimer;

        public NewVideoWatcher(ILibraryManager libraryManager, ITaskManager taskManager, ILogManager logManager,
            MediaBrowser.Common.Configuration.IApplicationPaths appPaths, MediaBrowser.Model.Serialization.IJsonSerializer json)
        {
            _libraryManager = libraryManager;
            _taskManager = taskManager;
            _log = logManager.GetLogger("SubRemover");
            _checkpoints = new CheckpointStore(appPaths.PluginConfigurationsPath, json);
        }

        // "...start it again when Emby starts": a run cut off by a restart left its progress behind.
        private void ResumeInterruptedRun()
        {
            try
            {
                var options = Plugin.Instance?.Options;
                if (options == null || !options.ResumeInterruptedRuns || !options.ResumeAfterRestart || !_checkpoints.Exists) return;
                _log.Info($"SubRemover: a run was interrupted; it continues in {ResumeAfterStart.TotalMinutes:0} minutes");
                _resumeTimer = new Timer(_ =>
                {
                    try
                    {
                        var busy = _taskManager.ScheduledTasks.Any(w => w.ScheduledTask is RemoveSubtitlesTask && w.State != TaskState.Idle);
                        if (!busy && _checkpoints.Exists) _taskManager.QueueIfNotRunning<RemoveSubtitlesTask>();
                    }
                    catch (Exception ex)
                    {
                        _log.ErrorException("SubRemover: couldn't continue the interrupted run", ex);
                    }
                }, null, ResumeAfterStart, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                _log.ErrorException("SubRemover: couldn't check for an interrupted run", ex);
            }
        }

        public void Run()
        {
            _libraryManager.ItemAdded += OnItemAdded;
            ResumeInterruptedRun();
        }

        public void Dispose()
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            lock (_timerLock) _timer?.Dispose();
            _resumeTimer?.Dispose();
        }

        // A video SubRemover rewrites keeps its path, so Emby updates it rather than adding it: no loop.
        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (Plugin.Instance?.Options.ProcessNewVideos != true) return;
            if (!(e.Item is Video video) || video.IsVirtualItem || string.IsNullOrEmpty(video.Path)) return;

            _waiting.Add(video.Path);
            StartTimer(WaitAfterLastVideo);
        }

        private void StartTimer(TimeSpan due)
        {
            lock (_timerLock)
            {
                if (_timer == null) _timer = new Timer(_ => StartRun(), null, due, Timeout.InfiniteTimeSpan);
                else _timer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }

        private void StartRun()
        {
            try
            {
                var busy = _taskManager.ScheduledTasks
                    .Any(w => (w.ScheduledTask is RemoveSubtitlesTask || w.ScheduledTask is RestoreBackupsTask) && w.State != TaskState.Idle);
                if (busy)
                {
                    StartTimer(WaitWhileBusy);
                    return;
                }

                var videos = _waiting.Take();
                // Switched off while they were waiting: drop them; the next full run gets to them.
                if (videos == null || Plugin.Instance?.Options.ProcessNewVideos != true) return;
                _log.Info($"SubRemover: {videos.Count} new video{(videos.Count == 1 ? "" : "s")} added to the library; starting a run for them");
                Plugin.Instance.NewVideos.AddRange(videos);
                _taskManager.QueueIfNotRunning<RemoveSubtitlesTask>();
            }
            catch (Exception ex)
            {
                _log.ErrorException("SubRemover: couldn't start a run for new videos", ex);
            }
        }
    }
}
