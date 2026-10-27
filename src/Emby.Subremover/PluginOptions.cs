using System.Collections.Generic;
using System.ComponentModel;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;

namespace Emby.Subremover
{
    // Emby builds the plugin's settings page automatically from these properties.
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Subremover";

        public override string EditorDescription =>
            "Removes unwanted embedded subtitle tracks from your videos. Run it from Scheduled Tasks → Subremover. " +
            "Leave Test mode on for your first run and check the Emby log to see what would change.";

        [DisplayName("Test mode (dry run)")]
        [Description("When on, nothing is changed: the task only writes to the Emby server log what it WOULD do. Turn off when the log looks right.")]
        public bool DryRun { get; set; } = true;

        [DisplayName("Libraries to process")]
        [Description("Only videos in these libraries are touched.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryChoices))]
        public string Libraries { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> LibraryChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Quick setup: keep English and subtitles with no language")]
        [Description("Tick this and click Save to select only English below and turn on 'Keep subtitles with no language set'. It unticks itself afterwards.")]
        public bool ApplyEnglishPreset { get; set; }

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

        [DisplayName("Extract kept text subtitles to .srt files")]
        [Description("Saves kept text subtitles (SRT/ASS/etc.) as files next to the video, which Emby picks up as external subtitles. Picture-based subtitles (PGS/VobSub) can't be extracted to text.")]
        public bool ExtractTextSubtitles { get; set; }

        [DisplayName("...and remove the extracted tracks from the video")]
        [VisibleCondition(nameof(ExtractTextSubtitles), SimpleCondition.IsTrue)]
        public bool RemoveExtractedFromVideo { get; set; }

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

        [DisplayName("Re-check every video on next run")]
        [Description("Forgets which files were already processed. Switches itself back off after the run.")]
        [IsAdvanced]
        public bool ReprocessAll { get; set; }
    }
}
