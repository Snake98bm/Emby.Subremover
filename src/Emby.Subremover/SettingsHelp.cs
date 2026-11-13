
using System.Linq;

namespace Emby.Subremover
{
    /// <summary>
    /// The Help at the bottom of each settings tab: the full explanation of each setting on that tab, in tab order. The
    /// settings themselves only carry a one-line description.
    /// </summary>
    internal static class SettingsHelp
    {
        // The settings page adds which Tesseract the server has to this entry (SettingsLayout.Page).
        internal const SettingsTabId OcrModelTab = SettingsTabId.SubtitleFiles;
        internal const string OcrModelSetting = "OCR model";

        internal static readonly (SettingsTabId Tab, string Setting, string Text)[] Entries =
        {
            (SettingsTabId.Run, "Run now",
                "Saves your settings and starts a run. The status under the button shows progress, and the log appears on the Logs tab. You can also run or schedule it from Settings > Scheduled Tasks > SubRemover."),
            (SettingsTabId.Run, "Test mode (dry run)",
                "When on, nothing is changed: each run only writes to its log what it WOULD do. Leave it on for your first run and turn it " +
                "off when the log looks right. Test mode checks the same videos a real run would."),
            (SettingsTabId.Run, "Libraries to process",
                "Only videos in the ticked libraries are checked."),
            (SettingsTabId.Run, "Max videos to change per run",
                "A run stops after changing this many videos. 0 means no limit. Use 1 to 5 while testing; each change can take several " +
                "minutes over the network. Runs for new videos don't use it: they do every new video."),
            (SettingsTabId.Run, "Only process paths containing",
                "Only videos whose full path contains this text are checked; capitals don't matter. 'My Movie (2020)' matches only that " +
                "movie, '\\Movies\\' only your Movies folder. For several, separate them with ';' (not commas, titles contain those): " +
                "'My Movie (2020); My Show - S01E02'. Leave empty for everything. Restore doesn't use it."),
            (SettingsTabId.Run, "Process new videos as they're added",
                "When Emby adds a video (real-time monitoring or a library scan), SubRemover checks it with these same settings, " +
                "Test mode included. It waits until no new video has come in for 3 minutes, so a whole season is one run with one log. " +
                "Only videos in the libraries to process and matching the path filter count. 'Max videos to change per run' doesn't apply: " +
                "every new video is done. It never runs next to another run: it waits and goes after."),
            (SettingsTabId.Run, "Continue an interrupted run where it stopped",
                "A run notes every few videos how far it got. If it's stopped, or Emby restarts in the middle, the next run with the " +
                "same settings (Test mode included) skips the videos already checked and finishes the rest; its log says where it " +
                "picked up. Changing a setting, or 'Re-check every video', starts over. Runs for new videos always do their own list."),
            (SettingsTabId.Run, "...start it again when Emby starts",
                "After Emby restarts, an interrupted run is started again by itself about 3 minutes later. Off: it waits for the next " +
                "Run now or scheduled run."),
            (SettingsTabId.Run, "Videos to check at once",
                "Checking a video (reading its tracks, the language check, deciding what to do) runs for this many videos side by " +
                "side, which makes Test mode runs several times faster. Changing a video (rewriting it, OCR) still happens one at a " +
                "time: it's limited by the speed of your disk or network share, and OCR already uses two CPU cores. Everything " +
                "SubRemover starts runs at below-normal priority, so playback comes first. Use 1 to check one video at a time."),
            (SettingsTabId.Run, "Show in the server sidebar menu",
                "Lists SubRemover under Advanced in the server sidebar. It's always under Plugins too. Reload the page (or restart Emby) " +
                "after changing this."),
            (SettingsTabId.Run, "Re-check every video on next run",
                "Forgets which videos were already done, including ones you restored, so the next real run checks all of them again; it " +
                "then switches itself off. In test mode it only includes them and forgets nothing."),
            (SettingsTabId.Subtitles, "Quick setup",
                "Ticks only English under 'Subtitle languages to keep', turns on 'Keep subtitles with no language set', and saves."),
            (SettingsTabId.Subtitles, "Subtitle languages to keep",
                "Tick every language you want to keep. Embedded subtitles in any language that isn't ticked are removed. All spellings of " +
                "a language match, e.g. ger, deu and de."),
            (SettingsTabId.Subtitles, "Keep subtitles with no language set",
                "Tracks tagged 'und' (undefined) are often English, so keeping them is the safe choice. With 'Guess the language' on, " +
                "they're read first."),
            (SettingsTabId.Subtitles, "Guess the language of subtitles with no language set",
                "Reads the first 15 minutes of text subtitles tagged 'und' and guesses their language from common words, or from the " +
                "alphabet (Greek, Hebrew, Thai, Korean, Japanese, Chinese). A track it can tell counts as that language: kept or removed " +
                "like any other, and a kept one gets the language written into the video so Emby shows it. Tracks it can't tell stay as " +
                "they are; picture subtitles (PGS/DVD) can't be read. Tracks that share a language with another one are read too: one " +
                "whose text is clearly another language counts as that language (wrong tags are common in multi-language releases). Text " +
                "that describes sounds or names speakers, like SDH, gets '.sdh' in its file name."),
            (SettingsTabId.Subtitles, "...only to name extracted files",
                "For subtitles with no language set. A guess never changes the video: its tracks stay exactly as they are. (Tracks " +
                "tagged with the wrong language have their own setting below.) The guess only decides the names of extracted " +
                "files: a track guessed to be in a language you keep is extracted as e.g. 'Movie.eng.srt', and one guessed to be another " +
                "language gets no file. Needs 'Extract kept text subtitles' on (Subtitle files tab)."),
            (SettingsTabId.Subtitles, "Subtitles tagged with the wrong language",
                "SubRemover reads the start of a text subtitle and calls its tag wrong only when it clearly is: the text is in another " +
                "writing system (Greek, Hebrew, Chinese...), or another language's common words far outnumber the tagged one's, or " +
                "almost none of the words are in the tagged language (at least 30 words read). Close neighbours, like Bosnian read as " +
                "Croatian, keep their tag; forced and commentary tracks are never checked. Leave them changes nothing (the log lists " +
                "them). Fix the tag writes the language the text reads as into the video, so your keep and remove choices go by the " +
                "real language: a track tagged English that reads as Bulgarian is removed if you keep only English, and one tagged " +
                "Hindi that reads as English is kept. A track that clearly isn't its tagged language but can't be identified gets no " +
                "language."),
            (SettingsTabId.Subtitles, "...remove the ones whose real language can't be told",
                "With Fix the tag: a track that clearly isn't its tagged language, but whose real language can't be told, is removed " +
                "instead of kept with no language. Tracks that never had a language aren't affected; they follow 'Keep subtitles with " +
                "no language set'."),
            (SettingsTabId.Subtitles, "...check every text subtitle",
                "Normally only tracks without a language, and tracks whose language tag another track in the same video shares, are " +
                "read: that's where wrong tags are common (e.g. 25 'English' tracks in 25 languages). On: every text subtitle is read, " +
                "so a video whose only 'English' track is really Spanish is caught too. Each video takes a few seconds longer."),
            (SettingsTabId.Subtitles, "Keep forced subtitles in any language",
                "Forced subtitles only show the foreign-language lines of a film (signs, a scene in another language). With this on, they " +
                "stay even in languages you don't keep."),
            (SettingsTabId.Subtitles, "Remove SDH / hearing-impaired subtitles",
                "SDH subtitles also describe sounds and name speakers; closed captions (titles with 'CC') count as SDH. An SDH track is " +
                "only removed when the video also has a regular track in that language. If SDH is the only English track, for example, " +
                "it's kept."),
            (SettingsTabId.Subtitles, "Remove commentary subtitles",
                "Removes subtitle tracks marked as commentary or titled 'Commentary', even in a language you keep."),
            (SettingsTabId.Subtitles, "Make the regular subtitle track the default",
                "Emby turns on the subtitle track a video marks as default. When that's an SDH, CC or commentary track, the default moves " +
                "to the regular track in the same language. A forced default stays. Videos where only this changes are rewritten too."),
            (SettingsTabId.Subtitles, "Default subtitle language",
                "Overrides the video: the regular subtitle in this language becomes the default (the flag in the video and the '.default' " +
                "file name), even if the video marks another track or none, so Emby turns it on. When the language only has SDH, the SDH " +
                "track gets it. Only the languages you keep are listed; save to update the list. Automatic: the video decides."),
            (SettingsTabId.SubtitleFiles, "Extract kept text subtitles to .srt files",
                "Saves kept text subtitles (SRT, ASS and similar) as files next to the video, which Emby shows as external subtitles. " +
                "Picture subtitles (PGS/DVD) can't be extracted as text; see OCR. Subtitles with no language set stay in the video."),
            (SettingsTabId.SubtitleFiles, "...and remove the extracted tracks from the video",
                "Once a track is saved as a file, it's taken out of the video. Off: it stays in the video as well."),
            (SettingsTabId.SubtitleFiles, "Mark the regular subtitle file as default",
                "Names the extracted or OCR file of the regular track like 'Movie.eng.default.srt', so Emby turns that file on instead of " +
                "another track. When a language only has SDH, that file gets it: 'Movie.eng.sdh.default.srt'. One file per video. Files " +
                "that already exist keep their name."),
            (SettingsTabId.SubtitleFiles, "Prefer subtitle files that are already there",
                "When a subtitle file in a language you keep is already next to the video and SubRemover didn't make it (e.g. downloaded " +
                "by Emby), SubRemover makes no file of its own in that language and takes the default flag off the embedded tracks in " +
                "that language, so Emby turns that file on. The embedded tracks stay in the video: if the file turns out wrong, pick them " +
                "in the player."),
            (SettingsTabId.SubtitleFiles, "Download hash-matched subtitles before changing a video",
                "Emby's subtitle downloaders (such as OpenSubtitles) find a perfect match by the video's hash, and changing the video " +
                "changes the hash. So before a video is changed for the first time, SubRemover saves its original hash and asks Emby's " +
                "downloaders for perfect matches, but only when the video has no built-in text subtitle (.srt-like) in a language you " +
                "keep or with no language set: then each language you keep that has no subtitle file is looked up. The best match is saved the way Emby's own search saves it. If the search fails, the " +
                "video is left unchanged for the next run. AudioRemover does the same before it changes a video SubRemover hasn't, so " +
                "the order the two run in doesn't matter. Needs a subtitle downloader set up in Emby. Test mode only logs what would be " +
                "looked up. After a failed search nothing more is asked for an hour. To get them for the whole library before any video is changed, use the buttons: Count hash-matched " +
                "subtitles asks the downloaders and downloads nothing; Download hash-matched subtitles saves them and every video's " +
                "original hash. Neither changes a video. A download run stops at the daily limit or when the downloader refuses " +
                "and carries on where it stopped next time. Also under Scheduled Tasks → SubRemover, where it follows Test mode."),
            (SettingsTabId.SubtitleFiles, "...at most this many downloads a day",
                "Caps the hash-matched subtitles downloaded each day, by SubRemover and AudioRemover together, counted by the UTC day " +
                "the way OpenSubtitles counts. It leaves the rest of the downloader's allowance (20 a day on a free OpenSubtitles " +
                "account) for Emby's own downloads. Once it's used up, a video that still needs a look-up is left unchanged until the " +
                "next day; videos that need none are changed as usual. 0 means no limit."),
            (SettingsTabId.SubtitleFiles, "Remove ads from subtitle files",
                "Downloaded subtitle files often carry ads and credits: \"Support us and become VIP member to remove all ads from " +
                "www.OpenSubtitles.org\", \"Synced and corrected by ... www.addic7ed.com\", \"Watch Movies, TV Series and Live " +
                "Sports Signup Here -> ...\". Not only at the start and end: some sites put them in the middle of the film. With " +
                "this on, every run reads the .srt files next to the videos it checks (only new or changed ones, and all of them " +
                "again when the list changes) and takes those cues out, numbering the rest again. The file keeps its encoding, " +
                "and is backed up first like a video (Backups tab), so it's on the restore list. OCR files SubRemover made are " +
                "left alone. Test mode only logs what would go. Subtitles inside the video aren't changed."),
            (SettingsTabId.SubtitleFiles, "Text to remove",
                "One entry per line. A cue with a line containing any of them is taken out whole. A word or phrase matches whole " +
                "words, ignoring capitals: 'yts' takes \"Downloaded from YTS.MX\" but not \"Bytes\", and a space also matches '.', " +
                "'-' or '_'. /…/ is a regular expression, e.g. /downloaded from\\s+www\\./. An entry that would also take " +
                "ordinary dialogue (\"I love you.\", \"Thank you.\", ...) or is too slow is skipped, and the run log says why. " +
                "Clear the box and save to get the starting list back."),
            (SettingsTabId.SubtitleFiles, "...also remove \"Subtitles by\" and \"Synced by\" credits",
                "Lines that start with a credit: \"Subtitles by…\", \"Sync and corrections by…\", \"Resync by…\", \"Ripped by…\", " +
                "\"Created and Encoded by…\", \"Synchro: …\", after any '-', '=' or '[' in front. Only at the start of a line, so " +
                "dialogue like \"He was corrected by his teacher\" stays."),
            (SettingsTabId.SubtitleFiles, "...also remove TV captioning credits",
                "The credits broadcast captions end with, like \"Captioned by Media Access Group at WGBH access.wgbh.org\" or " +
                "\"Captioning sponsored by …\". They're part of the original captions, not ads, so they stay unless this is on."),
            (SettingsTabId.SubtitleFiles, "Test run / Remove ads now",
                "Go through every .srt file next to the videos in your libraries (and path filter), whatever was checked before. " +
                "Test run changes nothing, whatever Test mode is set to: it only lists in its log what would be taken out, file by " +
                "file and line by line. Remove takes it out, backing each file up, and logs every line it took out. Open the log " +
                "opens the last one's log in a new window (also on the Logs tab). " +
                "Neither changes a video, and neither runs next to a run or a restore. Both also look for ads the list misses: a " +
                "web or e-mail address, or a credit-like line at the start or end, in at least two different films or shows. One " +
                "show's own plug for its website isn't suggested. Tick the suggestions you want, click Add to the list, then " +
                "Save. Also under Scheduled Tasks → SubRemover, where it follows Test mode."),
            (SettingsTabId.SubtitleFiles, "Experimental: convert picture subtitles to text (OCR)",
                "Reads kept picture subtitles (PGS/DVD) with OCR and saves them as text next to the video, e.g. 'Movie.pgs.eng.srt'. The " +
                "picture track stays in the video. Skipped when the video also has a text subtitle in that language, and for commentary. " +
                "The language data (about 4 MB per language) is downloaded the first time. OCR can misread words."),
            (SettingsTabId.SubtitleFiles, "If the OCR subtitle file already exists",
                "Keep existing leaves an OCR file from an earlier run alone. Replace makes it again, every run. Redo if made by an " +
                "older OCR makes it again only when SubRemover's OCR has improved since (each version of the plugin that reads " +
                "better says so in its release notes), or when it was made with a weaker OCR model than the one chosen now; " +
                "files from before SubRemover 0.7.7 count as older. It also covers OCR you picked in Emby's subtitle search: " +
                "SubRemover remembers those and redoes the file Emby saved, unless you deleted or edited it."),
            (OcrModelTab, OcrModelSetting,
                "Fast uses Tesseract's fast model (tessdata_fast, about 4 MB per language). Accurate uses its best model (tessdata_best, " +
                "about 15 MB per language, a little slower); it reads some words better and others worse. Both runs the two at the same " +
                "time on two CPU cores and, for each subtitle, keeps the reading with fewer misread words. The models are Tesseract's " +
                "LSTM models from github.com/tesseract-ocr, downloaded the first time they're needed; Tesseract itself is built into " +
                "Emby's ffmpeg. Used by the OCR setting above and by the subtitle search."),
            (SettingsTabId.SubtitleFiles, "Offer OCR in Emby's subtitle search",
                "Adds 'OCR of the built-in subtitles' to the results when you search for subtitles in Emby (Search for Subtitles in the " +
                "player, or a video's ... menu > Edit subtitles > Search). Picking it reads that picture track with OCR, which takes a few " +
                "minutes; a message on the player says when it starts and finishes, and Emby saves the text like a downloaded subtitle. " +
                "To keep it manual, leave SubRemover OCR unticked under Subtitle downloaders in your library settings."),
            (SettingsTabId.Backups, "Backup of the original file",
                "Next to original saves 'movie.mkv.subremover.bak' beside the video (Emby ignores it). Backup folder copies originals to " +
                "the folder you choose. None deletes the original, which isn't recommended: without a backup a change can't be undone."),
            (SettingsTabId.Backups, "Backup folder",
                "Where originals are copied when 'Backup of the original file' is Backup folder."),
            (SettingsTabId.Backups, "Delete backups older than (days)",
                "Refers to the backups of original videos that 'Backup of the original file' makes before SubRemover changes a " +
                "video: 'movie.mkv.subremover.bak' next to it, or the copy in the backup folder. Age counts from when the backup " +
                "was made. At the end of each run, backups older than this are deleted and drop off 'Restore originals from " +
                "backups', so those videos can no longer be put back. Test mode only logs what it would delete. 0 keeps them " +
                "forever. Subtitle-file backups have their own setting below."),
            (SettingsTabId.Backups, "Delete subtitle file backups older than (days)",
                "Backups of .srt files that had ads taken out ('Movie.en.srt.subremover.bak' next to it, or in the backup folder). " +
                "They're a few dozen KB each, so they can be kept much longer than video backups. 0 keeps them forever."),
            (SettingsTabId.Backups, "Restore originals from backups",
                "Lists every video that has a backup, newest first, with nothing selected. 'Show' picks all backups, video files only, " +
                "or subtitle files (.srt with ads taken out) only. Select the ones to restore, then click 'Yes, " +
                "restore...': each one is replaced by its original, undoing what SubRemover changed. Subtitle files SubRemover saved stay. " +
                "Test mode doesn't apply. Later runs leave restored videos alone. Moved a video, or its whole folder or library, on " +
                "the share? The next run finds it again by its file name among the videos Emby has (same size and date for videos " +
                "already done), and its backup, 'already done' and 'restored' records and the subtitle files SubRemover made for it " +
                "follow it, so it isn't checked again and can still be restored. A backup next to the video has to have moved with " +
                "it. A renamed file counts as a new video."),
            (SettingsTabId.Logs, "Logs",
                "Shows the last run. Pick an older run to see its log. The links under it open the whole log in a new window or " +
                "download it. The newest 30 are kept."),
        };

        /// <summary>The help entries for one tab, in tab order.</summary>
        internal static (SettingsTabId Tab, string Setting, string Text)[] For(SettingsTabId tab) => Entries.Where(e => e.Tab == tab).ToArray();

    }
}
