using System.Collections.Generic;
using System.ComponentModel;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace Emby.Subremover
{
    public enum ExistingOcrFile
    {
        KeepExisting,
        Replace
    }

    // Emby builds the plugin's settings page from these properties, in this order.
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "SubRemover v" + Plugin.VersionText;

        public override string EditorDescription =>
            "Removes unwanted embedded subtitle tracks from your videos. Leave Test mode on for your first run, " +
            "then check the subremover log under Settings → Logs to see what would change.";

        [DisplayName("")]
        [Description("Saves your settings, then starts the task. You can also run it from Scheduled Tasks → SubRemover.")]
        public ButtonItem RunNowButton { get; set; } = new ButtonItem("Run now")
        {
            CommandId = SettingsPageView.RunNowCommand,
            Icon = IconNames.play_arrow
        };

        [DisplayName("")]
        public StatusItem RunStatus { get; set; } = new StatusItem("Status", "Not run yet");

        [DisplayName("Test mode (dry run)")]
        [Description("When on, nothing is changed: the task only writes to its log (Settings → Logs → subremover-…) what it WOULD do. Turn off when the log looks right.")]
        public bool DryRun { get; set; } = true;

        [DisplayName("Libraries to process")]
        [Description("Only videos in these libraries are touched.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryChoices))]
        public string Libraries { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> LibraryChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("")]
        [Description("Selects only English below, turns on 'Keep subtitles with no language set', and saves.")]
        public ButtonItem QuickSetupButton { get; set; } = new ButtonItem("Quick setup: English + no language")
        {
            CommandId = SettingsPageView.QuickSetupCommand,
            Icon = IconNames.language
        };

        [DisplayName("Subtitle languages to keep")]
        [Description("Tick every language you want to keep. Embedded subtitles in any language that isn't ticked are removed. Matches all spellings, e.g. ger, deu and de.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LanguageChoices))]
        public string KeepLanguages { get; set; } = "eng";

        [Browsable(false)]
        public List<EditorSelectOption> LanguageChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Keep subtitles with no language set")]
        [Description("Tracks tagged 'und' (undefined) are often English. Safer to keep them.")]
        public bool KeepUndefined { get; set; } = true;

        [DisplayName("Keep forced subtitles in any language")]
        public bool KeepForcedAnyLanguage { get; set; }

        [DisplayName("Remove SDH / hearing-impaired subtitles")]
        [Description("Removes them even when they are in a language you keep.")]
        public bool RemoveHearingImpaired { get; set; }

        [DisplayName("Remove commentary subtitles")]
        [Description("Removes subtitle tracks marked as commentary or titled 'Commentary', even when they are in a language you keep.")]
        public bool RemoveCommentary { get; set; }

        [DisplayName("Extract kept text subtitles to .srt files")]
        [Description("Saves kept text subtitles (SRT/ASS/etc.) as files next to the video, which Emby picks up as external subtitles. Picture-based subtitles (PGS/VobSub) can't be extracted to text.")]
        public bool ExtractTextSubtitles { get; set; }

        [DisplayName("...and remove the extracted tracks from the video")]
        [VisibleCondition(nameof(ExtractTextSubtitles), SimpleCondition.IsTrue)]
        public bool RemoveExtractedFromVideo { get; set; }

        [DisplayName("Experimental: convert picture subtitles to text (OCR)")]
        [Description("Reads kept picture subtitles (PGS/DVD) with OCR and saves them as text next to the video, e.g. 'Movie.pgs.eng.srt'. The picture track stays in the video. Downloads the language data (about 4 MB per language) the first time. OCR can misread words.")]
        public bool OcrPictureSubtitles { get; set; }

        [DisplayName("If the OCR subtitle file already exists")]
        [VisibleCondition(nameof(OcrPictureSubtitles), SimpleCondition.IsTrue)]
        public ExistingOcrFile OcrExistingFile { get; set; } = ExistingOcrFile.KeepExisting;

        [DisplayName("Backup of the original file")]
        [Description("NextToOriginal saves 'movie.mkv.subremover.bak' beside the file (Emby ignores it). BackupFolder copies originals to the folder below. None deletes the original — not recommended.")]
        public BackupMode BackupMode { get; set; } = BackupMode.NextToOriginal;

        [DisplayName("Backup folder")]
        [EditFolderPicker]
        [VisibleCondition(nameof(BackupMode), ValueCondition.IsEqual, BackupMode.BackupFolder)]
        public string BackupFolder { get; set; } = string.Empty;

        [DisplayName("Delete backups older than (days)")]
        [Description("0 keeps backups forever. Use the 'Restore originals from backups' task to undo changes.")]
        [MinValue(0)]
        [MaxValue(3650)]
        public int BackupRetentionDays { get; set; }

        [DisplayName("Max videos to change per run")]
        [Description("0 = no limit. Set to 1–5 while testing.")]
        [MinValue(0)]
        [MaxValue(100000)]
        public int MaxItemsPerRun { get; set; } = 5;

        [DisplayName("Only process paths containing")]
        [Description("Optional. e.g. a single movie folder name, to test on one title.")]
        public string OnlyPathContains { get; set; } = string.Empty;

        [DisplayName("Show in the server sidebar menu")]
        [Description("Lists SubRemover under Advanced in the server sidebar. It's always under Plugins too. Reload the page (or restart Emby) after changing this.")]
        public bool ShowInSidebar { get; set; } = true;

        [DisplayName("Re-check every video on next run")]
        [Description("Forgets which files were already processed, including videos you restored from backups. Switches itself back off after the run.")]
        [IsAdvanced]
        public bool ReprocessAll { get; set; }

        [DisplayName("")]
        public CaptionItem LogsCaption { get; set; } = new CaptionItem("Run logs");

        [DisplayName("Run log")]
        [Description("Shows the last run. Pick an older run to see its log. The newest 30 are kept.")]
        [SelectItemsSource(nameof(LogChoices))]
        [AutoPostBack(SettingsPageView.ShowLogCommand, nameof(SelectedLog))]
        public string SelectedLog { get; set; }

        [Browsable(false)]
        public List<EditorSelectOption> LogChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("")]
        [Description("Read only: anything typed here is not saved.")]
        [EditMultiline(20)]
        public string LogText { get; set; }

        /// <summary>Drops the lists and log text that are only filled in for display, so they never go into the settings file.</summary>
        internal void ClearDisplayOnlyFields()
        {
            LibraryChoices = new List<EditorSelectOption>();
            LanguageChoices = new List<EditorSelectOption>();
            LogChoices = new List<EditorSelectOption>();
            SelectedLog = null;
            LogText = null;
        }
    }
}
