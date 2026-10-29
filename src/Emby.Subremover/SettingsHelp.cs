
using Emby.Web.GenericEdit.Elements;

namespace Emby.Subremover
{
    /// <summary>
    /// The Help section at the bottom of the settings page: the full explanation of each setting, in page order. The
    /// settings themselves only carry a one-line description ending in "(Help ↓)".
    /// </summary>
    internal static class SettingsHelp
    {
        internal static readonly (string Setting, string Text)[] Entries =
        {
            ("Run now",
                "Saves your settings and starts a run. The status under the button shows progress, and the run log appears at the bottom " +
                "of the page. You can also run or schedule it from Settings > Scheduled Tasks > SubRemover."),
            ("Test mode (dry run)",
                "When on, nothing is changed: each run only writes to its log what it WOULD do. Leave it on for your first run and turn it " +
                "off when the log looks right. Test mode checks the same videos a real run would."),
            ("Libraries to process",
                "Only videos in the ticked libraries are checked."),
            ("Quick setup",
                "Ticks only English under 'Subtitle languages to keep', turns on 'Keep subtitles with no language set', and saves."),
            ("Subtitle languages to keep",
                "Tick every language you want to keep. Embedded subtitles in any language that isn't ticked are removed. All spellings of " +
                "a language match, e.g. ger, deu and de."),
            ("Keep subtitles with no language set",
                "Tracks tagged 'und' (undefined) are often English, so keeping them is the safe choice. With 'Guess the language' on, " +
                "they're read first."),
            ("Guess the language of subtitles with no language set",
                "Reads the first 15 minutes of text subtitles tagged 'und' and guesses their language from common words, or from the " +
                "alphabet (Greek, Hebrew, Thai, Korean, Japanese, Chinese). A track it can tell counts as that language: kept or removed " +
                "like any other, and a kept one gets the language written into the video so Emby shows it. Tracks it can't tell stay as " +
                "they are; picture subtitles (PGS/DVD) can't be read. Tracks that share a language with another one are read too: one " +
                "whose text is clearly another language counts as that language (wrong tags are common in multi-language releases). Text " +
                "that describes sounds or names speakers, like SDH, gets '.sdh' in its file name."),
            ("...only to name extracted files",
                "A guess never changes the video: its tracks stay exactly as they are. The guess only decides the names of extracted " +
                "files: a track guessed to be in a language you keep is extracted as e.g. 'Movie.eng.srt', and one guessed to be another " +
                "language gets no file. Needs 'Extract kept text subtitles' on."),
            ("Keep forced subtitles in any language",
                "Forced subtitles only show the foreign-language lines of a film (signs, a scene in another language). With this on, they " +
                "stay even in languages you don't keep."),
            ("Remove SDH / hearing-impaired subtitles",
                "SDH subtitles also describe sounds and name speakers; closed captions (titles with 'CC') count as SDH. An SDH track is " +
                "only removed when the video also has a regular track in that language. If SDH is the only English track, for example, " +
                "it's kept."),
            ("Remove commentary subtitles",
                "Removes subtitle tracks marked as commentary or titled 'Commentary', even in a language you keep."),
            ("Make the regular subtitle track the default",
                "Emby turns on the subtitle track a video marks as default. When that's an SDH, CC or commentary track, the default moves " +
                "to the regular track in the same language. A forced default stays. Videos where only this changes are rewritten too."),
            ("Default subtitle language",
                "Overrides the video: the regular subtitle in this language becomes the default (the flag in the video and the '.default' " +
                "file name), even if the video marks another track or none, so Emby turns it on. When the language only has SDH, the SDH " +
                "track gets it. Only the languages you keep are listed; save to update the list. Automatic: the video decides."),
            ("Extract kept text subtitles to .srt files",
                "Saves kept text subtitles (SRT, ASS and similar) as files next to the video, which Emby shows as external subtitles. " +
                "Picture subtitles (PGS/DVD) can't be extracted as text; see OCR. Subtitles with no language set stay in the video."),
            ("...and remove the extracted tracks from the video",
                "Once a track is saved as a file, it's taken out of the video. Off: it stays in the video as well."),
            ("Experimental: convert picture subtitles to text (OCR)",
                "Reads kept picture subtitles (PGS/DVD) with OCR and saves them as text next to the video, e.g. 'Movie.pgs.eng.srt'. The " +
                "picture track stays in the video. Skipped when the video also has a text subtitle in that language, and for commentary. " +
                "The language data (about 4 MB per language) is downloaded the first time. OCR can misread words."),
            ("Offer OCR in Emby's subtitle search",
                "Adds 'OCR of the built-in subtitles' to the results when you search for subtitles in Emby (Search for Subtitles in the " +
                "player, or a video's ... menu > Edit subtitles > Search). Picking it reads that picture track with OCR, which takes a few " +
                "minutes; a message on the player says when it starts and finishes, and Emby saves the text like a downloaded subtitle. " +
                "To keep it manual, leave SubRemover OCR unticked under Subtitle downloaders in your library settings."),
            ("If the OCR subtitle file already exists",
                "KeepExisting leaves an OCR file from an earlier run alone. Replace makes it again."),
            ("Mark the regular subtitle file as default",
                "Names the extracted or OCR file of the regular track like 'Movie.eng.default.srt', so Emby turns that file on instead of " +
                "another track. When a language only has SDH, that file gets it: 'Movie.eng.sdh.default.srt'. One file per video. Files " +
                "that already exist keep their name."),
            ("Prefer subtitle files that are already there",
                "When a subtitle file in a language you keep is already next to the video and SubRemover didn't make it (e.g. downloaded " +
                "by Emby), SubRemover makes no file of its own in that language and takes the default flag off the embedded tracks in " +
                "that language, so Emby turns that file on. The embedded tracks stay in the video: if the file turns out wrong, pick them " +
                "in the player."),
            ("Backup of the original file",
                "NextToOriginal saves 'movie.mkv.subremover.bak' beside the video (Emby ignores it). BackupFolder copies originals to " +
                "the folder you choose. None deletes the original, which isn't recommended: without a backup a change can't be undone."),
            ("Backup folder",
                "Where originals are copied when 'Backup of the original file' is BackupFolder."),
            ("Delete backups older than (days)",
                "Backups older than this are deleted at the start of a run. 0 keeps them forever."),
            ("Max videos to change per run",
                "A run stops after changing this many videos. 0 means no limit. Use 1 to 5 while testing; each change can take several " +
                "minutes over the network."),
            ("Only process paths containing",
                "Only videos whose full path contains this text are checked; capitals don't matter. 'My Movie (2020)' matches only that " +
                "movie, '\\Movies\\' only your Movies folder. For several, separate them with ';' (not commas, titles contain those): " +
                "'My Movie (2020); My Show - S01E02'. Leave empty for everything. Restore doesn't use it."),
            ("Restore originals from backups",
                "Lists every video that has a backup, newest first, with nothing selected. Select the ones to restore, then click 'Yes, " +
                "restore...': each one is replaced by its original, undoing what SubRemover changed. Subtitle files SubRemover saved stay. " +
                "Test mode doesn't apply. Later runs leave restored videos alone."),
            ("Show in the server sidebar menu",
                "Lists SubRemover under Advanced in the server sidebar. It's always under Plugins too. Reload the page (or restart Emby) " +
                "after changing this."),
            ("Re-check every video on next run",
                "Forgets which videos were already done, including ones you restored, so the next real run checks all of them again; it " +
                "then switches itself off. In test mode it only includes them and forgets nothing."),
            ("Run log",
                "Shows the last run. Pick an older run to see its log. The newest 30 are kept."),
        };

        // The setting's name in bold with its explanation wrapped underneath; plain, without a status icon.
        internal static StatusItem Item(int index) => new StatusItem(Entries[index].Setting, Entries[index].Text, ItemStatus.None);
    }
}
