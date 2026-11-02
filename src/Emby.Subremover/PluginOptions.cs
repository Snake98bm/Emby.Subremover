using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
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
            "Removes unwanted embedded subtitle tracks from your videos. Leave Test mode on for your first run and check its log " +
            "at the bottom of the page. Each setting is explained under Help, at the very bottom.";

        [DisplayName("")]
        [Description("Saves your settings and starts a run.")]
        public ButtonItem RunNowButton { get; set; } = new ButtonItem("Run now")
        {
            CommandId = SettingsPageView.RunNowCommand,
            Icon = IconNames.play_arrow
        };

        // Our own Save, next to Run now; Emby's Save at the bottom of the page is hidden (SettingsPageView.ShowSave).
        [DisplayName("")]
        public ButtonItem SaveButton { get; set; } = new ButtonItem("Save")
        {
            CommandId = SettingsPageView.SaveCommand,
            Icon = IconNames.save
        };

        [DisplayName("")]
        public StatusItem RunStatus { get; set; } = new StatusItem("Status", "Not run yet");

        [DisplayName("Test mode (dry run)")]
        [Description("Nothing is changed; the log shows what would happen.")]
        public bool DryRun { get; set; } = true;

        [DisplayName("Libraries to process")]
        [Description("Only videos in these libraries are checked.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryChoices))]
        public string Libraries { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> LibraryChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("")]
        [Description("Keeps English and subtitles with no language, and saves.")]
        public ButtonItem QuickSetupButton { get; set; } = new ButtonItem("Quick setup: English + no language")
        {
            CommandId = SettingsPageView.QuickSetupCommand,
            Icon = IconNames.language
        };

        [DisplayName("Subtitle languages to keep")]
        [Description("Subtitles in any other language are removed.")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LanguageChoices))]
        public string KeepLanguages { get; set; } = "eng";

        [Browsable(false)]
        public List<EditorSelectOption> LanguageChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Keep subtitles with no language set")]
        [Description("They're often English, so keeping them is safe.")]
        public bool KeepUndefined { get; set; } = true;

        [DisplayName("Guess the language of subtitles with no language set")]
        [Description("Reads the subtitle text to tell its language and spot SDH.")]
        public bool GuessLanguages { get; set; }

        [DisplayName("...only to name extracted files (never remove or change tracks)")]
        [Description("The video is never changed; guesses only name extracted files.")]
        [VisibleCondition(nameof(GuessLanguages), SimpleCondition.IsTrue)]
        public bool GuessForFilesOnly { get; set; }

        [DisplayName("Keep forced subtitles in any language")]
        [Description("Foreign-lines-only subtitles stay in every language.")]
        public bool KeepForcedAnyLanguage { get; set; }

        [DisplayName("Remove SDH / hearing-impaired subtitles")]
        [Description("Only when a regular track in that language stays.")]
        public bool RemoveHearingImpaired { get; set; }

        [DisplayName("Remove commentary subtitles")]
        [Description("Even in languages you keep.")]
        public bool RemoveCommentary { get; set; }

        [DisplayName("Make the regular subtitle track the default")]
        [Description("Moves the default off SDH, CC or commentary tracks.")]
        public bool MakeRegularDefault { get; set; } = true;

        [DisplayName("Default subtitle language")]
        [Description("This language's subtitle becomes the default. Automatic: the video decides.")]
        [SelectItemsSource(nameof(DefaultLanguageChoices))]
        public string DefaultLanguage { get; set; } = string.Empty;

        [Browsable(false)]
        public List<EditorSelectOption> DefaultLanguageChoices { get; set; } = new List<EditorSelectOption>();

        [DisplayName("Extract kept text subtitles to .srt files")]
        [Description("Saves kept text subtitles as files next to the video.")]
        public bool ExtractTextSubtitles { get; set; }

        [DisplayName("...and remove the extracted tracks from the video")]
        [Description("Off: the track stays in the video as well.")]
        [VisibleCondition(nameof(ExtractTextSubtitles), SimpleCondition.IsTrue)]
        public bool RemoveExtractedFromVideo { get; set; }

        [DisplayName("Experimental: convert picture subtitles to text (OCR)")]
        [Description("Turns picture subtitles (PGS/DVD) into text files.")]
        public bool OcrPictureSubtitles { get; set; }

        [DisplayName("Offer OCR in Emby's subtitle search")]
        [Description("Adds an OCR choice to Emby's Search for Subtitles.")]
        public bool OfferOcrInSubtitleSearch { get; set; }

        [DisplayName("If the OCR subtitle file already exists")]
        [VisibleCondition(nameof(OcrPictureSubtitles), SimpleCondition.IsTrue)]
        public ExistingOcrFile OcrExistingFile { get; set; } = ExistingOcrFile.KeepExisting;

        [DisplayName("Mark the regular subtitle file as default")]
        [Description("Names the regular subtitle file '.default' so Emby turns it on.")]
        public bool MarkDefaultInName { get; set; }

        [DisplayName("Prefer subtitle files that are already there (e.g. downloaded by Emby)")]
        [Description("Leaves a language to a subtitle file that's already there.")]
        public bool PreferExistingFiles { get; set; }

        [DisplayName("Backup of the original file")]
        [Description("Where the original goes before a video is changed.")]
        public BackupMode BackupMode { get; set; } = BackupMode.NextToOriginal;

        [DisplayName("Backup folder")]
        [EditFolderPicker]
        [VisibleCondition(nameof(BackupMode), ValueCondition.IsEqual, BackupMode.BackupFolder)]
        public string BackupFolder { get; set; } = string.Empty;

        [DisplayName("Delete backups older than (days)")]
        [Description("0 keeps them forever.")]
        [MinValue(0)]
        [MaxValue(3650)]
        public int BackupRetentionDays { get; set; }

        [DisplayName("Max videos to change per run")]
        [Description("0 = no limit. Use 1–5 while testing.")]
        [MinValue(0)]
        [MaxValue(100000)]
        public int MaxItemsPerRun { get; set; } = 5;

        [DisplayName("Only process paths containing")]
        [Description("Only paths containing this text. Several: separate with ';'.")]
        public string OnlyPathContains { get; set; } = string.Empty;

        [DisplayName("Process new videos as they're added")]
        [Description("Checks videos Emby adds, a few minutes after they arrive.")]
        public bool ProcessNewVideos { get; set; }

        [DisplayName("")]
        [Description("Lists the videos with a backup to choose from.")]
        public ButtonItem RestoreButton { get; set; } = NewRestoreButton();

        internal static ButtonItem NewRestoreButton(int? confirmCount = null) => new ButtonItem(
            confirmCount == null ? "Restore originals from backups…" : confirmCount == 1 ? "Yes, restore this video" : "Yes, restore the selected videos")
        {
            CommandId = confirmCount == null ? SettingsPageView.RestoreCommand : SettingsPageView.RestoreConfirmCommand,
            Icon = confirmCount == null ? IconNames.restore : IconNames.warning,
            // Emby asks this before sending the command.
            ConfirmationPrompt = confirmCount == null ? null
                : "Restore the selected videos? Each one is replaced by its original from the backup, undoing what SubRemover changed."
        };

        [DisplayName("")]
        public StatusItem RestoreStatus { get; set; } = new StatusItem(Plugin.RestoreCaption, "");

        [DisplayName("Videos that will be restored")]
        [Description("Switch Restore on for each video to restore.")]
        [VisibleCondition(nameof(RestoreListShown), SimpleCondition.IsNotNullOrEmpty)]
        public GenericItemList RestoreItems { get; set; } = new GenericItemList();

        // Set while the restore list is on the page, so the list and its buttons show.
        [Browsable(false)]
        public string RestoreListShown { get; set; }

        [DisplayName("")]
        [VisibleCondition(nameof(RestoreListShown), SimpleCondition.IsNotNullOrEmpty)]
        public ButtonItem RestoreSelectAllButton { get; set; } = NewSelectAllButton(allSelected: false);

        /// <summary>One button: "Unselect all" while every video is selected, otherwise "Select all".</summary>
        internal static ButtonItem NewSelectAllButton(bool allSelected) => new ButtonItem(allSelected ? "Unselect all" : "Select all")
        {
            CommandId = SettingsPageView.RestoreSelectAllCommand,
            Icon = allSelected ? IconNames.check_box_outline_blank : IconNames.check_box
        };

        [DisplayName("Show in the server sidebar menu")]
        [Description("Also lists SubRemover in the server sidebar.")]
        public bool ShowInSidebar { get; set; } = true;

        [DisplayName("Re-check every video on next run")]
        [Description("Checks every video again on the next real run.")]
        [IsAdvanced]
        public bool ReprocessAll { get; set; }

        [DisplayName("")]
        public CaptionItem LogsCaption { get; set; } = new CaptionItem("Log files");

        [DisplayName("Logs")]
        [Description("Pick an older run to see its log.")]
        [SelectItemsSource(nameof(LogChoices))]
        [AutoPostBack(SettingsPageView.ShowLogCommand, nameof(SelectedLog))]
        public string SelectedLog { get; set; }

        [Browsable(false)]
        public List<EditorSelectOption> LogChoices { get; set; } = new List<EditorSelectOption>();

        // "Open this log in a new window" and "Download this log", for the log picked above.
        [DisplayName("")]
        public GenericItemList LogLinks { get; set; } = new GenericItemList();

        [DisplayName("")]
        [EditMultiline(20)]
        public string LogText { get; set; }

        [DisplayName("")]
        public CaptionItem HelpCaption { get; set; } = new CaptionItem("Help");

        // Help: each setting's name in bold with its explanation underneath, in page order. Emby has no way to add these
        // from a list, so there's one entry per setting; a test checks there's one for every entry in SettingsHelp. Always read
        // from SettingsHelp: the settings file keeps a copy of each, and an older version's copy would show stale or repeated help.
        [DisplayName("")] public StatusItem Help01 { get => SettingsHelp.Item(0); set { } }
        [DisplayName("")] public StatusItem Help02 { get => SettingsHelp.Item(1); set { } }
        [DisplayName("")] public StatusItem Help03 { get => SettingsHelp.Item(2); set { } }
        [DisplayName("")] public StatusItem Help04 { get => SettingsHelp.Item(3); set { } }
        [DisplayName("")] public StatusItem Help05 { get => SettingsHelp.Item(4); set { } }
        [DisplayName("")] public StatusItem Help06 { get => SettingsHelp.Item(5); set { } }
        [DisplayName("")] public StatusItem Help07 { get => SettingsHelp.Item(6); set { } }
        [DisplayName("")] public StatusItem Help08 { get => SettingsHelp.Item(7); set { } }
        [DisplayName("")] public StatusItem Help09 { get => SettingsHelp.Item(8); set { } }
        [DisplayName("")] public StatusItem Help10 { get => SettingsHelp.Item(9); set { } }
        [DisplayName("")] public StatusItem Help11 { get => SettingsHelp.Item(10); set { } }
        [DisplayName("")] public StatusItem Help12 { get => SettingsHelp.Item(11); set { } }
        [DisplayName("")] public StatusItem Help13 { get => SettingsHelp.Item(12); set { } }
        [DisplayName("")] public StatusItem Help14 { get => SettingsHelp.Item(13); set { } }
        [DisplayName("")] public StatusItem Help15 { get => SettingsHelp.Item(14); set { } }
        [DisplayName("")] public StatusItem Help16 { get => SettingsHelp.Item(15); set { } }
        [DisplayName("")] public StatusItem Help17 { get => SettingsHelp.Item(16); set { } }
        [DisplayName("")] public StatusItem Help18 { get => SettingsHelp.Item(17); set { } }
        [DisplayName("")] public StatusItem Help19 { get => SettingsHelp.Item(18); set { } }
        [DisplayName("")] public StatusItem Help20 { get => SettingsHelp.Item(19); set { } }
        [DisplayName("")] public StatusItem Help21 { get => SettingsHelp.Item(20); set { } }
        [DisplayName("")] public StatusItem Help22 { get => SettingsHelp.Item(21); set { } }
        [DisplayName("")] public StatusItem Help23 { get => SettingsHelp.Item(22); set { } }
        [DisplayName("")] public StatusItem Help24 { get => SettingsHelp.Item(23); set { } }
        [DisplayName("")] public StatusItem Help25 { get => SettingsHelp.Item(24); set { } }
        [DisplayName("")] public StatusItem Help26 { get => SettingsHelp.Item(25); set { } }
        [DisplayName("")] public StatusItem Help27 { get => SettingsHelp.Item(26); set { } }
        [DisplayName("")] public StatusItem Help28 { get => SettingsHelp.Item(27); set { } }
        [DisplayName("")] public StatusItem Help29 { get => SettingsHelp.Item(28); set { } }
        [DisplayName("")] public StatusItem Help30 { get => SettingsHelp.Item(29); set { } }

        /// <summary>Drops the lists and log text that are only filled in for display, so they never go into the settings file.</summary>
        // Filled in only to show the page; not settings.
        private static readonly HashSet<string> DisplayOnly = new HashSet<string> { nameof(SelectedLog), nameof(LogText) };

        /// <summary>Every setting as "Name: value", in page order, for the top of each run log.</summary>
        /// <param name="libraryNames">Emby's library names by id, so the libraries read "Movies (12082)" rather than just the id.</param>
        internal List<string> DescribeSettings(IDictionary<string, string> libraryNames = null) =>
            GetType().GetProperties()
                .Where(p => p.CanWrite && !DisplayOnly.Contains(p.Name)
                            && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(string) || p.PropertyType == typeof(int) || p.PropertyType.IsEnum)
                            && p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
                .Select(p =>
                {
                    var label = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
                    var value = p.GetValue(this);
                    if (p.Name == nameof(Libraries) && libraryNames != null) value = NameLibraries(Libraries, libraryNames);
                    var text = value is bool b ? (b ? "on" : "off")
                        : value is string s ? (s.Length == 0 ? "(empty)" : s)
                        : value?.ToString() ?? "(empty)";
                    return $"{(string.IsNullOrEmpty(label) ? p.Name : label)}: {text}";
                })
                .ToList();

        /// <summary>"Movies (12082), TV Shows (12083)": the ids the setting holds, each with its name when Emby still has it.</summary>
        internal static string NameLibraries(string ids, IDictionary<string, string> names) =>
            string.Join(", ", (ids ?? string.Empty).Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Select(id => names.TryGetValue(id, out var name) ? $"{name} ({id})" : id));

        internal void ClearDisplayOnlyFields()
        {
            LibraryChoices = new List<EditorSelectOption>();
            LanguageChoices = new List<EditorSelectOption>();
            DefaultLanguageChoices = new List<EditorSelectOption>();
            LogChoices = new List<EditorSelectOption>();
            RestoreItems = new GenericItemList();
            RestoreListShown = null;
            SelectedLog = null;
            LogText = null;
            LogLinks = new GenericItemList();
        }
    }
}
