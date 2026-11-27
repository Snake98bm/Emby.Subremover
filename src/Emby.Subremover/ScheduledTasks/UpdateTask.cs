using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Subremover.ScheduledTasks
{
    /// <summary>
    /// Once a day: checks GitHub for a newer SubRemover release and, when "Install updates automatically" is on, puts it in
    /// the plugins folder. Emby uses it after its next restart, which this task can do when nothing is playing.
    /// </summary>
    public class UpdateTask : IScheduledTask
    {
        private readonly IApplicationHost _host;
        private readonly ISessionManager _sessions;
        private readonly ITaskManager _tasks;
        private readonly ILogger _log;

        // How long a restart waits for playback and other tasks to finish, and how often it looks.
        internal static readonly TimeSpan RestartWaitLimit = TimeSpan.FromHours(4);
        internal static readonly TimeSpan RestartWaitStep = TimeSpan.FromMinutes(5);

        public UpdateTask(IApplicationHost host, ISessionManager sessions, ITaskManager tasks, ILogManager logManager)
        {
            _host = host;
            _sessions = sessions;
            _tasks = tasks;
            _log = logManager?.GetLogger("SubRemover");
        }

        public string Name => "Check for SubRemover updates";
        public string Key => "SubremoverCheckForUpdates";
        public string Description => "Checks GitHub for a newer SubRemover release and, when 'Install updates automatically' is on, installs it. It starts working when Emby restarts.";
        public string Category => "SubRemover";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks }
        };

        /// <summary>What the running task is doing, for the settings page; null when it isn't running.</summary>
        internal static volatile string Live;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            // Set by "Install now" on the settings page: install even when installing automatically is off.
            var manual = plugin.TakeUpdateInstallClick();
            var options = plugin.Options;
            try
            {
                Live = "Checking GitHub for a newer release…";
                var updater = plugin.CreateUpdater();
                var outcome = await updater.Run(options.AutoUpdate || manual, manual, m => _log.Info("{0}", m), cancellationToken).ConfigureAwait(false);
                progress.Report(80);

                if (outcome == UpdateOutcome.Installed || outcome == UpdateOutcome.WaitingForRestart)
                {
                    try { _host.NotifyPendingRestart(); } catch (Exception ex) { _log.ErrorException("SubRemover: couldn't tell Emby a restart is needed", ex); }
                    if (options.AutoUpdate && options.RestartForUpdate)
                        await RestartWhenIdle(plugin, updater.Latest, cancellationToken).ConfigureAwait(false);
                }
                progress.Report(100);
            }
            finally
            {
                Live = null;
            }
        }

        private async Task RestartWhenIdle(Plugin plugin, PluginVersion version, CancellationToken cancellationToken)
        {
            if (!_host.CanSelfRestart)
            {
                _log.Info("SubRemover: v{0} is installed; this Emby can't restart itself, so restart it to start using it", version);
                return;
            }

            var waitUntil = DateTime.UtcNow + RestartWaitLimit;
            while (true)
            {
                var busy = WhatsBusy();
                if (busy == null) break;
                if (DateTime.UtcNow >= waitUntil)
                {
                    _log.Info("SubRemover: v{0} is installed; Emby wasn't restarted because {1}. It starts working at the next restart", version, busy);
                    return;
                }
                Live = $"v{version} is installed. Waiting to restart Emby: {busy}…";
                await Task.Delay(RestartWaitStep, cancellationToken).ConfigureAwait(false);
            }

            var store = plugin.UpdateStore;
            var state = store.Load();
            state.RestartedFor = version.ToString();
            store.Save(state);
            _log.Info("SubRemover: restarting Emby to start using v{0}", version);
            Live = $"Restarting Emby to start using v{version}…";
            _host.Restart();
        }

        /// <summary>Why Emby shouldn't restart now, or null.</summary>
        private string WhatsBusy()
        {
            if (TaskGate.IsBusy) return "another SubRemover task is running";
            // New videos waiting for their run are only remembered until Emby stops.
            if (Plugin.Instance?.NewVideos.Count > 0) return "new videos are waiting for their run";
            var playing = _sessions.Sessions.Count(s => s.NowPlayingItem != null);
            if (playing > 0) return playing == 1 ? "someone is watching" : playing + " people are watching";
            if (_tasks.ScheduledTasks.Any(w => w.State == TaskState.Running && !(w.ScheduledTask is UpdateTask)))
                return "other scheduled tasks are running";
            return null;
        }
    }
}
