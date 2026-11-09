using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

    [Route("/SubRemover/Backups", "GET", Summary = "Videos with a backup, for the restore list", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetBackups : IReturn<BackupListDto>
    {
    }

    [Route("/SubRemover/Restore", "POST", Summary = "Restores the selected videos from their backups", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class StartRestore : IReturn<StatusDto>
    {
        /// <summary>The list the selection was made on, as GetBackups returned it.</summary>
        public string Key { get; set; }

        /// <summary>The selected rows, e.g. "0,3,5".</summary>
        public string Indexes { get; set; }
    }

    [Route("/SubRemover/LogView", "GET", Summary = "One run log, for the Logs tab", IsHidden = true)]
    [Authenticated(Roles = "Admin")]
    public class GetLogView : IReturn<LogViewDto>
    {
        public string Name { get; set; }
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

    public class BackupListDto
    {
        public string Refusal { get; set; }
        public string Key { get; set; }
        public int Total { get; set; }
        public List<BackupRowDto> Items { get; set; }
    }

    public class BackupRowDto
    {
        public string File { get; set; }
        public string Folder { get; set; }
        public string Made { get; set; }
        public bool Missing { get; set; }
    }

    public class LogViewDto
    {
        public string Name { get; set; }
        public string Text { get; set; }
        public string OpenUrl { get; set; }
        public string DownloadUrl { get; set; }
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

        public object Get(GetBackups request)
        {
            var (refusal, backups) = Plugin.RestorePlan();
            return new BackupListDto
            {
                Refusal = refusal,
                Key = RestoreKey(backups),
                Total = backups.Count,
                Items = backups.Take(Plugin.RestoreListLimit).Select(b => new BackupRowDto
                {
                    File = Path.GetFileName(b.OriginalPath),
                    Folder = Path.GetDirectoryName(b.OriginalPath),
                    Made = b.CreatedUtc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture),
                    Missing = !File.Exists(b.BackupPath)
                }).ToList()
            };
        }

        // Restores only from the list the person looked at: if the backups changed since, nothing is restored.
        public object Post(StartRestore request)
        {
            var plugin = Plugin;
            var (refusal, backups) = plugin.RestorePlan();
            if (refusal != null) return Warning(refusal);
            if (string.IsNullOrEmpty(request.Key) || request.Key != RestoreKey(backups))
                return Warning("Nothing restored: the list of backups changed. Click 'Restore originals from backups…' to see it again.");

            var chosen = Plugin.SelectedBackups(backups, ParseIndexes(request.Indexes));
            if (chosen.Count == 0) return Warning("Nothing restored: no video was selected.");
            return StatusDto.From(plugin.StartRestore(chosen));
        }

        public object Get(GetLogView request)
        {
            var logs = SafeLogList();
            var name = logs.Contains(request.Name) ? request.Name : logs.FirstOrDefault();
            if (name == null) return new LogViewDto { Text = "No runs yet." };

            var key = Plugin.LogLinkKey.Current(DateTime.UtcNow);
            string text;
            try { text = RunLog.ReadForDisplay(_logDirectory, name); }
            catch (Exception ex) { text = "Couldn't read the run log: " + ex.Message; }
            return new LogViewDto
            {
                Name = name,
                Text = text,
                OpenUrl = GetSubremoverLog.Link(name, key, download: false),
                DownloadUrl = GetSubremoverLog.Link(name, key, download: true)
            };
        }

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
