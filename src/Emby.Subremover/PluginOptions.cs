using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Emby.Subremover.Core;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;

namespace Emby.Subremover
{
    public enum WrongLanguageTags
    {
        LeaveThem,
        FixTheTag
    }

    public enum ExistingOcrFile
    {
        KeepExisting,
        Replace,
        RedoIfMadeByAnOlderOcr
    }

    // The saved settings. Each one's label, description and limits are what the settings page shows (SettingsLayout.cs puts
    // them on tabs) and what each run log lists. Lists to choose from come from the server (SettingsApi.cs).
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "SubRemover v" + Plugin.VersionText;

        public override string EditorDescription => PageDescription;

        // Where the page gets each list's choices.
        internal const string LibrariesSource = "Libraries";
        internal const string LanguagesSource = "Languages";
        internal const string DefaultLanguageSource = "DefaultLanguage";

        internal const string PageDescription =
            "Removes unwanted embedded subtitle tracks from your videos. Leave Test mode on for your first run and check its log " +
            "on the Logs tab. Each tab explains its settings at the bottom.";

        [DisplayName("Test mode (dry run)")]
        [Description("Nothing is changed; the log shows what would happen.")]
        public bool DryRun { get; set; } = true;

        [DisplayName("Libraries to process")]
        [Description("Only videos in these libraries are checked.")]
        [EditMultilSelect]
        [SelectItemsSource(LibrariesSource)]
        public string Libraries { get; set; } = string.Empty;

        [DisplayName("Subtitle languages to keep")]
        [Description("Subtitles in any other language are removed.")]
        [EditMultilSelect]
        [SelectItemsSource(LanguagesSource)]
        public string KeepLanguages { get; set; } = "eng";

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

        [DisplayName("Subtitles tagged with the wrong language")]
        [Description("When a subtitle's text clearly isn't its tagged language: leave the tag, or fix it.")]
        public WrongLanguageTags WrongLanguageTags { get; set; }

        [DisplayName("...remove the ones whose real language can't be told")]
        [Description("Off: they get no language and follow \"Keep subtitles with no language set\".")]
        [VisibleCondition(nameof(WrongLanguageTags), ValueCondition.IsEqual, WrongLanguageTags.FixTheTag)]
        public bool RemoveWrongTagsLeftUndefined { get; set; }

        [DisplayName("...check every text subtitle, not only tags two or more tracks share")]
        [Description("Also catches a lone mistagged track. Reads a little of each video.")]
        [VisibleCondition(nameof(WrongLanguageTags), ValueCondition.IsEqual, WrongLanguageTags.FixTheTag)]
        public bool CheckEveryTextSubtitle { get; set; }

        /// <summary>Which settings update this file has had (see OptionsStore.Upgrade); not shown.</summary>
        [Browsable(false)]
        public int OptionsVersion { get; set; }

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
        [SelectItemsSource(DefaultLanguageSource)]
        public string DefaultLanguage { get; set; } = string.Empty;

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

        [DisplayName("OCR model")]
        [Description("Both reads every subtitle with both models at once and keeps the better reading.")]
        public OcrModels OcrModel { get; set; } = OcrModels.Both;

        [DisplayName("If the OCR subtitle file already exists")]
        [Description("Redo if made by an older OCR also covers files saved from the subtitle search.")]
        public ExistingOcrFile OcrExistingFile { get; set; } = ExistingOcrFile.KeepExisting;

        [DisplayName("Mark the regular subtitle file as default")]
        [Description("Names the regular subtitle file '.default' so Emby turns it on.")]
        public bool MarkDefaultInName { get; set; }

        [DisplayName("Prefer subtitle files that are already there (e.g. downloaded by Emby)")]
        [Description("Leaves a language to a subtitle file that's already there.")]
        public bool PreferExistingFiles { get; set; }

        [DisplayName("Download hash-matched subtitles before changing a video")]
        [Description("Changing a video changes its hash; first asks Emby's subtitle downloaders for perfect matches. Also used by AudioRemover.")]
        public bool DownloadHashMatchedSubtitles { get; set; } = true;

        [DisplayName("...at most this many downloads a day")]
        [Description("0 = no limit. Leaves the rest of the provider's daily allowance (OpenSubtitles: 20 on a free account) for Emby. A video over the limit is left unchanged until the next day.")]
        [VisibleCondition(nameof(DownloadHashMatchedSubtitles), SimpleCondition.IsTrue)]
        [MinValue(0)]
        [MaxValue(10000)]
        public int HashDownloadsPerDay { get; set; } = 10;

        [DisplayName("Remove ads from subtitle files")]
        [Description("Takes ad lines like \"Support us and become VIP member\" out of the .srt files next to your videos, during runs. Each changed file is backed up first.")]
        public bool RemoveSubtitleAds { get; set; }

        [DisplayName("Text to remove")]
        [Description("One per line. A subtitle line with any of these goes, with the rest of its cue. /…/: a regular expression. Anything else: that word or phrase.")]
        [EditMultiline(10)]
        public string SubtitleAdText { get; set; } = SubtitleAdRules.DefaultList;

        [DisplayName("...also remove \"Subtitles by\" and \"Synced by\" credits")]
        [Description("Lines that start like \"Subtitles by…\", \"Sync and corrections by…\" or \"Ripped by…\".")]
        public bool RemoveSubtitleCredits { get; set; } = true;

        [DisplayName("...also remove TV captioning credits")]
        [Description("\"Captioned by Media Access Group at WGBH\", \"Captioning sponsored by…\". Off: they stay.")]
        public bool RemoveCaptionCredits { get; set; }

        [DisplayName("Backup of the original file")]
        [Description("Where the original goes before a video is changed.")]
        public BackupMode BackupMode { get; set; } = BackupMode.NextToOriginal;

        [DisplayName("Backup folder")]
        [EditFolderPicker]
        [VisibleCondition(nameof(BackupMode), ValueCondition.IsEqual, BackupMode.BackupFolder)]
        public string BackupFolder { get; set; } = string.Empty;

        [DisplayName("Delete backups older than (days)")]
        [Description("The backups of original videos made before SubRemover changed them ('movie.mkv.subremover.bak', or the copy in the backup folder). Once deleted, that video can't be restored. 0 keeps them forever.")]
        [MinValue(0)]
        [MaxValue(3650)]
        public int BackupRetentionDays { get; set; }

        [DisplayName("Delete subtitle file backups older than (days)")]
        [Description("Backups of .srt files that had ads taken out. They're tiny. 0 keeps them forever.")]
        [MinValue(0)]
        [MaxValue(36500)]
        public int SubtitleBackupRetentionDays { get; set; }

        internal SubtitleAdRules AdRules() => new SubtitleAdRules(SubtitleAdText, RemoveSubtitleCredits, RemoveCaptionCredits);

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

        [DisplayName("Videos to check at once")]
        [Description("1–8. Checking runs side by side at low priority; changing videos stays one at a time.")]
        [MinValue(1)]
        [MaxValue(8)]
        public int VideosAtOnce { get; set; } = 4;

        [DisplayName("Continue an interrupted run where it stopped")]
        [Description("A run that was stopped, or cut off by an Emby restart, skips the videos it already checked.")]
        public bool ResumeInterruptedRuns { get; set; } = true;

        [DisplayName("...start it again when Emby starts")]
        [Description("Picks the interrupted run up a few minutes after Emby starts.")]
        [VisibleCondition(nameof(ResumeInterruptedRuns), SimpleCondition.IsTrue)]
        public bool ResumeAfterRestart { get; set; } = true;

        [DisplayName("Show in the server sidebar menu")]
        [Description("Also lists SubRemover in the server sidebar.")]
        public bool ShowInSidebar { get; set; } = true;

        [DisplayName("Re-check every video on next run")]
        [Description("Checks every video again on the next real run.")]
        [IsAdvanced]
        public bool ReprocessAll { get; set; }

        /// <summary>Every setting as "Name: value", in page order, for the top of each run log.</summary>
        /// <param name="libraryNames">Emby's library names by id, so the libraries read "Movies (12082)" rather than just the id.</param>
        internal List<string> DescribeSettings(IDictionary<string, string> libraryNames = null) =>
            GetType().GetProperties()
                .Where(p => p.CanWrite
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
    }
}
