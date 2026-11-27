using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.Subremover
{
    // ------------------------------------------------------------------------------------
    // Administrator-only endpoints for the settings page (Configuration/settings.html).
    // ------------------------------------------------------------------------------------

    [Route("/SubRemover/Page", "GET", Summary = "Everything the settings page shows", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetSettingsPage : IReturn<PageDto>
    {
    }

    [Route("/SubRemover/Settings", "POST", Summary = "Saves the settings page", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class SaveSettings : IReturn<SaveResultDto>
    {
        /// <summary>The settings as a JSON object of name → value text.</summary>
        public string Json { get; set; }
    }

    [Route("/SubRemover/Run", "POST", Summary = "Starts a run", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class StartRun : IReturn<StatusDto>
    {
    }

    [Route("/SubRemover/Status", "GET", Summary = "The run status line", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetRunStatus : IReturn<StatusDto>
    {
    }

    [Route("/SubRemover/HashSubtitles", "POST", Summary = "Counts or downloads hash-matched subtitles", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class StartHashSubtitles : IReturn<StatusDto>
    {
        public bool CountOnly { get; set; }

        /// <summary>Forget where a stopped run got to, rather than carry on from there.</summary>
        public bool StartOver { get; set; }
    }

    [Route("/SubRemover/HashSubtitles/Status", "GET", Summary = "The hash-matched subtitles status line", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetHashStatus : IReturn<StatusDto>
    {
    }

    [Route("/SubRemover/SubtitleAds", "POST", Summary = "Finds or removes ads in subtitle files", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class StartSubtitleAds : IReturn<AdStatusDto>
    {
        public bool FindOnly { get; set; }
    }

    [Route("/SubRemover/SubtitleAds/Status", "GET", Summary = "The subtitle-ads status line and suggestions", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetSubtitleAdsStatus : IReturn<AdStatusDto>
    {
    }

    [Route("/SubRemover/SubtitleTiming", "POST", Summary = "Scans or fixes the timing of subtitle files", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class StartSubtitleTiming : IReturn<TimingStatusDto>
    {
        public bool TestOnly { get; set; }

        /// <summary>Forget where a stopped run got to and measure every file again.</summary>
        public bool StartOver { get; set; }
    }

    [Route("/SubRemover/SubtitleTiming/Status", "GET", Summary = "The subtitle-timing status line", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetSubtitleTimingStatus : IReturn<TimingStatusDto>
    {
    }

    [Route("/SubRemover/Backups", "GET", Summary = "Videos with a backup, for the restore list", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetBackups : IReturn<BackupListDto>
    {
        /// <summary>"all" (default), "videos" or "subtitles".</summary>
        public string Show { get; set; }

        /// <summary>Why the backup was made (a BackupReasons value), or "any" (default).</summary>
        public string Why { get; set; }
    }

    [Route("/SubRemover/Restore", "POST", Summary = "Restores the selected videos from their backups", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class StartRestore : IReturn<StatusDto>
    {
        /// <summary>The list the selection was made on, as GetBackups returned it.</summary>
        public string Key { get; set; }

        /// <summary>The selected rows, e.g. "0,3,5".</summary>
        public string Indexes { get; set; }

        /// <summary>The "Show" the list was made with, as passed to GetBackups.</summary>
        public string Show { get; set; }

        /// <summary>The "Why" the list was made with, as passed to GetBackups.</summary>
        public string Why { get; set; }
    }

    [Route("/SubRemover/DeleteBackups", "POST", Summary = "Deletes the selected backups for good", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class DeleteBackups : IReturn<StatusDto>
    {
        /// <summary>The list the selection was made on, as GetBackups returned it.</summary>
        public string Key { get; set; }
        public string Indexes { get; set; }
        public string Show { get; set; }
        public string Why { get; set; }

        /// <summary>What was typed to confirm: must be "delete" (any capitals).</summary>
        public string Confirm { get; set; }
    }

    [Route("/SubRemover/LogView", "GET", Summary = "One run log, for the Logs tab", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetLogView : IReturn<LogViewDto>
    {
        public string Name { get; set; }

        /// <summary>"all" (default), "problems", or what it's about: Videos, SubtitleFiles, HashDownloads, Ocr.</summary>
        public string Show { get; set; }

        /// <summary>Leave out Debug lines.</summary>
        public bool HideDetails { get; set; }
    }

    [Route("/SubRemover/Failures", "GET", Summary = "Videos that failed in recent runs, for the Errors tab", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetFailures : IReturn<FailureListDto>
    {
    }

    [Route("/SubRemover/Failures/Retry", "POST", Summary = "Forgets the failures of the ticked videos and, if asked, retries them now", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class RetryFailures : IReturn<StatusDto>
    {
        /// <summary>The ticked videos' paths, one per line, as GetFailures listed them.</summary>
        public string Paths { get; set; }

        /// <summary>Start a run for them now; otherwise the next run tries them.</summary>
        public bool Now { get; set; }
    }

    public class FailureListDto
    {
        public int MaxAttempts { get; set; }
        public List<FailureRowDto> Items { get; set; }

        /// <summary>subremover-failures.log, newest lines first.</summary>
        public string LogText { get; set; }
        public string LogOpenUrl { get; set; }
        public string LogDownloadUrl { get; set; }
    }

    public class FailureRowDto
    {
        public string Path { get; set; }
        public string File { get; set; }
        public string Folder { get; set; }
        public int Count { get; set; }
        public bool Skipped { get; set; }
        public string FirstFailed { get; set; }
        public string LastFailed { get; set; }
        public string LastReason { get; set; }
        public List<FailureAttemptDto> Attempts { get; set; }
    }

    public class FailureAttemptDto
    {
        public string When { get; set; }
        public string Reason { get; set; }
        public string LogName { get; set; }
        public string LogUrl { get; set; }
    }

    public class PageDto
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public List<TabDto> Tabs { get; set; }
        public Dictionary<string, string> Values { get; set; }
        public List<OptionDto> Libraries { get; set; }
        public List<OptionDto> Languages { get; set; }
        public StatusDto Status { get; set; }
        public StatusDto HashStatus { get; set; }
        public AdStatusDto AdStatus { get; set; }
        public TimingStatusDto TimingStatus { get; set; }
        public string BackupCount { get; set; }
        public List<string> Logs { get; set; }
    }

    public class SaveResultDto
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> Values { get; set; }
    }

    public class StatusDto
    {
        public string Caption { get; set; }
        public string Text { get; set; }

        /// <summary>Emby's status names: None, InProgress, Succeeded, Failed, Warning, Unavailable, Unknown.</summary>
        public string State { get; set; }

        internal static StatusDto From(StatusItem item) =>
            new StatusDto { Caption = item?.Caption, Text = item?.StatusText, State = (item?.Status ?? ItemStatus.None).ToString() };
    }

    public class AdStatusDto
    {
        public StatusDto Status { get; set; }

        /// <summary>What the last Find or Remove suggests adding to "Text to remove".</summary>
        public List<AdSuggestionDto> Suggestions { get; set; }

        /// <summary>The last Find or Remove's log: its name and a link that opens it in a new window.</summary>
        public string LogName { get; set; }
        public string LogUrl { get; set; }

        internal static AdStatusDto Current(Plugin plugin) => new AdStatusDto
        {
            Status = StatusDto.From(plugin.AdStatus()),
            LogName = ScheduledTasks.SubtitleAdsTask.LastLogName,
            LogUrl = ScheduledTasks.SubtitleAdsTask.LastLogName == null ? null
                : GetSubremoverLog.Link(ScheduledTasks.SubtitleAdsTask.LastLogName, plugin.LogLinkKey.Current(DateTime.UtcNow), download: false),
            Suggestions = ScheduledTasks.SubtitleAdsTask.LastSuggestions.Select(s => new AdSuggestionDto
            {
                Entry = s.Entry,
                Titles = s.Titles,
                Example = s.Example,
                TitleNames = string.Join(", ", s.TitleNames.Take(8)) + (s.TitleNames.Count > 8 ? $" and {s.TitleNames.Count - 8} more" : "")
            }).ToList()
        };
    }

    public class TimingStatusDto
    {
        public StatusDto Status { get; set; }
        public string LogName { get; set; }
        public string LogUrl { get; set; }

        internal static TimingStatusDto Current(Plugin plugin) => new TimingStatusDto
        {
            Status = StatusDto.From(plugin.TimingStatus()),
            LogName = ScheduledTasks.SubtitleTimingTask.LastLogName,
            LogUrl = ScheduledTasks.SubtitleTimingTask.LastLogName == null ? null
                : GetSubremoverLog.Link(ScheduledTasks.SubtitleTimingTask.LastLogName, plugin.LogLinkKey.Current(DateTime.UtcNow), download: false)
        };
    }

    public class AdSuggestionDto
    {
        public string Entry { get; set; }
        public int Titles { get; set; }
        public string Example { get; set; }
        public string TitleNames { get; set; }
    }

    public class BackupListDto
    {
        public string Refusal { get; set; }
        public string Key { get; set; }
        public int Total { get; set; }
        public List<BackupRowDto> Items { get; set; }

        /// <summary>The reasons to filter by, with how many backups of the chosen kind have each.</summary>
        public List<ReasonDto> Reasons { get; set; }
    }

    public class ReasonDto
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public int Count { get; set; }
    }

    public class BackupRowDto
    {
        public string File { get; set; }
        public string Folder { get; set; }
        public string Made { get; set; }
        public bool Missing { get; set; }

        /// <summary>What SubRemover changed, one or two lines.</summary>
        public List<string> Changes { get; set; }

        /// <summary>Shown instead when nothing was recorded (older backups).</summary>
        public string ChangesNote { get; set; }
    }

    public class LogViewDto
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public string OpenUrl { get; set; }
        public string DownloadUrl { get; set; }

        /// <summary>How many entries each filter choice shows.</summary>
        public Dictionary<string, int> Counts { get; set; }
    }

    public class SettingsApi : IService
    {
        private readonly IJsonSerializer _json;
        private readonly string _logDirectory;
        private readonly IFfmpegManager _ffmpeg;

        public SettingsApi(IJsonSerializer json, IApplicationPaths appPaths, IFfmpegManager ffmpeg)
        {
            _json = json;
            _logDirectory = appPaths.LogDirectoryPath;
            _ffmpeg = ffmpeg;
        }

        // "Tesseract 5.2.0, built into Emby's ffmpeg ..." for the OCR model help; null if it can't be told.
        private string OcrEngine()
        {
            try { return Core.OcrEngineInfo.Describe(_ffmpeg?.FfmpegConfiguration?.EncoderPath); }
            catch (Exception) { return null; }
        }

        private static Plugin Plugin => Plugin.Instance ?? throw new InvalidOperationException("SubRemover isn't loaded.");

        public object Get(GetSettingsPage request)
        {
            var plugin = Plugin;
            var options = plugin.OptionsForEditing();
            return new PageDto
            {
                Title = "SubRemover v" + Plugin.VersionText,
                Description = PluginOptions.PageDescription,
                Tabs = SettingsLayout.Page(OcrEngine()),
                Values = SettingsLayout.Values(options),
                Libraries = plugin.LibraryChoices().Select(c => new OptionDto { Value = c.Value, Name = c.Name }).ToList(),
                Languages = Plugin.LanguageChoicesFor(options.KeepLanguages).Select(c => new OptionDto { Value = c.Value, Name = c.Name }).ToList(),
                Status = StatusDto.From(plugin.CurrentStatus()),
                HashStatus = StatusDto.From(plugin.HashStatus()),
                AdStatus = AdStatusDto.Current(plugin),
                TimingStatus = TimingStatusDto.Current(plugin),
                BackupCount = plugin.BackupCountText(),
                Logs = SafeLogList()
            };
        }

        public object Post(SaveSettings request)
        {
            var plugin = Plugin;
            Dictionary<string, string> values;
            try
            {
                values = string.IsNullOrEmpty(request.Json) ? new Dictionary<string, string>() : _json.DeserializeFromString<Dictionary<string, string>>(request.Json);
            }
            catch (Exception ex)
            {
                return new SaveResultDto { Ok = false, Message = "Couldn't read the settings: " + ex.Message };
            }

            var options = plugin.OptionsForEditing();
            var problem = SettingsLayout.Apply(options, values);
            if (problem == null)
            {
                try { options.ValidateOrThrow(); }
                catch (Exception ex) { problem = ex.Message; }
            }
            if (problem != null) return new SaveResultDto { Ok = false, Message = "Not saved: " + problem };

            plugin.Save(options);
            return new SaveResultDto { Ok = true, Message = "Saved.", Values = SettingsLayout.Values(plugin.OptionsForEditing()) };
        }

        public object Post(StartRun request) => StatusDto.From(Plugin.RunNow());

        public object Get(GetRunStatus request) => StatusDto.From(Plugin.CurrentStatus());

        public object Post(StartHashSubtitles request) => StatusDto.From(Plugin.StartHashRun(request.CountOnly, request.StartOver));

        public object Get(GetHashStatus request) => StatusDto.From(Plugin.HashStatus());

        public object Post(StartSubtitleAds request)
        {
            var started = Plugin.StartAdRun(request.FindOnly);
            var dto = AdStatusDto.Current(Plugin);
            dto.Status = StatusDto.From(started);
            return dto;
        }

        public object Get(GetSubtitleAdsStatus request) => AdStatusDto.Current(Plugin);

        public object Post(StartSubtitleTiming request)
        {
            var started = Plugin.StartTimingRun(request.TestOnly, request.StartOver);
            var dto = TimingStatusDto.Current(Plugin);
            dto.Status = StatusDto.From(started);
            return dto;
        }

        public object Get(GetSubtitleTimingStatus request) => TimingStatusDto.Current(Plugin);

        public object Get(GetBackups request)
        {
            var (refusal, backups, ofKind) = Plugin.RestorePlan(request.Show, request.Why);
            return new BackupListDto
            {
                Refusal = refusal,
                Key = RestoreKey(backups),
                Total = backups.Count,
                Reasons = BackupReasons.Counts(ofKind).Select(r => new ReasonDto { Value = r.Value, Label = r.Label, Count = r.Count }).ToList(),
                Items = backups.Take(Plugin.RestoreListLimit).Select(b => new BackupRowDto
                {
                    File = Path.GetFileName(b.OriginalPath),
                    Folder = Path.GetDirectoryName(b.OriginalPath),
                    Made = b.CreatedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
                    Missing = !File.Exists(b.BackupPath),
                    Changes = (b.Changes ?? new List<string>())
                        .Select(c => c == Core.BackupNotes.OldDefaultNote ? Core.BackupNotes.OldDefaultNoteShown : c).ToList(),
                    ChangesNote = b.Changes == null ? "Changes weren't recorded (backup made before 0.7.7)" : null
                }).ToList()
            };
        }

        // Restores only from the list the person looked at: if the backups changed since, nothing is restored.
        public object Post(StartRestore request)
        {
            var plugin = Plugin;
            var (refusal, backups, _) = plugin.RestorePlan(request.Show, request.Why);
            if (refusal != null) return Warning(refusal);
            if (string.IsNullOrEmpty(request.Key) || request.Key != RestoreKey(backups))
                return Warning("Nothing restored: the list of backups changed. Click 'Restore originals from backups…' to see it again.");

            var chosen = Plugin.SelectedBackups(backups, ParseIndexes(request.Indexes));
            if (chosen.Count == 0) return Warning("Nothing restored: no video was selected.");
            return StatusDto.From(plugin.StartRestore(chosen));
        }

        // Deletes only from the list the person looked at, and only after they typed the word.
        public object Post(DeleteBackups request)
        {
            if (!Plugin.IsDeleteConfirmed(request.Confirm)) return Warning("Nothing deleted: type Delete to confirm.");
            var plugin = Plugin;
            var (refusal, backups, _) = plugin.RestorePlan(request.Show, request.Why);
            if (refusal != null) return Warning(refusal.Replace("Nothing restored", "Nothing deleted"));
            if (string.IsNullOrEmpty(request.Key) || request.Key != RestoreKey(backups))
                return Warning("Nothing deleted: the list of backups changed. Click 'Restore originals from backups…' to see it again.");
            var chosen = Plugin.SelectedBackups(backups, ParseIndexes(request.Indexes));
            if (chosen.Count == 0) return Warning("Nothing deleted: no backup was selected.");
            return StatusDto.From(plugin.DeleteBackups(chosen));
        }

        public object Get(GetLogView request)
        {
            var logs = SafeLogList();
            var name = logs.Contains(request.Name) ? request.Name : logs.FirstOrDefault();
            if (name == null) return new LogViewDto { Text = "No runs yet." };

            var key = Plugin.LogLinkKey.Current(DateTime.UtcNow);
            string text;
            Dictionary<string, int> counts = null;
            try
            {
                var filtered = LogFilter.Apply(RunLog.ReadAll(_logDirectory, name), request.Show, request.HideDetails);
                text = RunLog.CutForDisplay(filtered.Text);
                counts = filtered.Counts;
            }
            catch (Exception ex) { text = "Couldn't read the run log: " + ex.Message; }
            return new LogViewDto
            {
                Name = name,
                Text = text,
                Counts = counts,
                OpenUrl = GetSubremoverLog.Link(name, key, download: false),
                DownloadUrl = GetSubremoverLog.Link(name, key, download: true)
            };
        }

        public object Get(GetFailures request)
        {
            var plugin = Plugin;
            var max = plugin.Options.MaxFailedAttempts;
            var key = plugin.LogLinkKey.Current(DateTime.UtcNow);
            var logs = new HashSet<string>(SafeLogList());
            string When(DateTime utc) => utc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
            return new FailureListDto
            {
                MaxAttempts = max,
                Items = plugin.FailedVideoList().Select(kv => new FailureRowDto
                {
                    Path = kv.Key,
                    File = Path.GetFileName(kv.Key),
                    Folder = Path.GetDirectoryName(kv.Key),
                    Count = kv.Value.Count,
                    Skipped = max > 0 && kv.Value.Count >= max,
                    FirstFailed = When(kv.Value.FirstUtc),
                    LastFailed = When(kv.Value.LastUtc),
                    LastReason = FailedVideos.Shorten(kv.Value.LastReason()),
                    Attempts = (kv.Value.Attempts ?? new List<FailedAttempt>()).AsEnumerable().Reverse().Select(a => new FailureAttemptDto
                    {
                        When = When(a.Utc),
                        Reason = FailedVideos.Shorten(a.Reason, 4000),
                        LogName = a.LogFile,
                        // Older run logs are deleted (30 kept); no link to one that's gone.
                        LogUrl = a.LogFile != null && logs.Contains(a.LogFile) ? GetSubremoverLog.Link(a.LogFile, key, download: false) : null
                    }).ToList()
                }).ToList(),
                LogText = plugin.FailureLog.ReadNewestFirst(),
                LogOpenUrl = GetSubremoverLog.Link(FailureLog.FileName, key, download: false),
                LogDownloadUrl = GetSubremoverLog.Link(FailureLog.FileName, key, download: true)
            };
        }

        public object Post(RetryFailures request) =>
            StatusDto.From(Plugin.RetryFailed((request.Paths ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries), request.Now));

        private List<string> SafeLogList()
        {
            try { return RunLog.ListFiles(_logDirectory).ToList(); }
            catch { return new List<string>(); }
        }

        private static StatusDto Warning(string text) =>
            new StatusDto { Caption = Plugin.RestoreCaption, Text = text, State = ItemStatus.Warning.ToString() };

        internal static IEnumerable<int> ParseIndexes(string indexes) =>
            (indexes ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1)
                .Where(i => i >= 0);

        /// <summary>Stands for the list of backups the page showed, so a restore can tell the list hasn't changed since.</summary>
        internal static string RestoreKey(IEnumerable<Core.BackupEntry> backups)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", backups.Select(b => b.OriginalPath))));
                return BitConverter.ToString(bytes, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
