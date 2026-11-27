
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
            (SettingsTabId.Run, "...but still download hash-matched subtitles and clean up subtitle files",
                "Lets Test mode cover only the videos. Videos are still never changed, but the scheduled Download hash-matched " +
                "subtitles task downloads perfect matches (up to the daily limit); a test run downloads them too for each video it " +
                "would change, first, while the video is still the original; and runs and the scheduled tasks take ads out of " +
                ".srt files and fix their timing, when Remove ads from subtitle files and Fix subtitle file timing are on. Each " +
                "changed .srt file is backed up first, so it's on the restore list."),
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
            (SettingsTabId.Run, "Check for updates now",
                "Asks GitHub for SubRemover's newest release. If it's newer and 'Install updates automatically' is on, it's " +
                "installed. Install now installs it either way. The check also runs by itself once a day at 4:30 AM; to change the " +
                "time, go to Scheduled Tasks > SubRemover > Check for SubRemover updates."),
            (SettingsTabId.Run, "Install updates automatically",
                "Once a day SubRemover asks GitHub (github.com/" + Core.GitHubReleaseSource.Repository + ") for its newest release. " +
                "If that's newer than the one running, its DLL is downloaded and checked: the size and SHA-256 GitHub lists, and " +
                "that it really is SubRemover with that version. Then it takes the place of the current DLL in Emby's plugins " +
                "folder. Emby only loads plugins when it starts, so the new version starts working at the next restart, and the " +
                "dashboard says a restart is needed. A copy of the DLL it replaced is kept in the plugins' configuration folder, " +
                "under subremover-update/previous. Off: the daily check only says here that a new version is out."),
            (SettingsTabId.Run, "...restart Emby to finish an update when nothing is playing",
                "After installing an update, restarts Emby once nobody is watching and no scheduled task is running. It waits up to " +
                "4 hours, otherwise the update waits for the next restart. Emby is restarted only once for each release. Some " +
                "setups (some Linux services and Docker) can't restart Emby from inside; restart it yourself there."),
            (SettingsTabId.Run, "Videos to check at once",
                "Checking a video (reading its tracks, the language check, deciding what to do) runs for this many videos side by " +
                "side, which makes Test mode runs several times faster. Changing a video (rewriting it, OCR) still happens one at a " +
                "time: it's limited by the speed of your disk or network share, and OCR already uses two CPU cores. Everything " +
                "SubRemover starts runs at below-normal priority, so playback comes first. Use 1 to check one video at a time."),
            (SettingsTabId.Run, "Show in the server sidebar menu",
                "Lists SubRemover in the Server section of the sidebar, next to Dashboard and Libraries. It's always under Plugins too. After changing it, click Save " +
                "and then hold Ctrl and press F5 (on a Mac: Cmd+Shift+R). That reloads the Emby page fully; a plain F5 can " +
                "still show the old menu."),
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
            (SettingsTabId.Subtitles, "...even when it's the only full subtitle in that language",
                "Removes SDH tracks in every case, also when no regular track in that language stays. A movie whose only full English " +
                "subtitle is SDH is then left with its forced English track (if it has one) or no English subtitles. Videos already " +
                "checked are checked again on the next run. Subtitle files made earlier (such as '.pgs.eng.sdh.srt' from OCR) aren't " +
                "deleted; delete them yourself if you don't want them."),
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
                "changes the hash. Uploaders sometimes file a subtitle under the wrong hash, so a match whose name is for another " +
                "title (another film, the first film for its sequel, another year, another episode) is skipped and logged. " +
                "Each download is also read: one whose text is another language, or (English) machine-translated with another " +
                "language's words all through it, is deleted straight away and the next match tried, up to 3 per language; " +
                "those count toward the daily limit. " +
                "Before a video is changed for the first time, SubRemover saves its original hash and asks Emby's " +
                "downloaders for perfect matches, but only when the video has no built-in text subtitle (.srt-like) in a language you " +
                "keep or with no language set: then each language you keep that has no subtitle file is looked up. The best match is saved the way Emby's own search saves it. If the search fails, the " +
                "video is left unchanged for the next run. AudioRemover does the same before it changes a video SubRemover hasn't, so " +
                "the order the two run in doesn't matter. Needs a subtitle downloader set up in Emby. Test mode only logs what would be " +
                "looked up. After a failed search nothing more is asked for an hour. To get them for the whole library before any video is changed, use the buttons: Count hash-matched " +
                "subtitles asks the downloaders and downloads nothing; Download hash-matched subtitles saves them and every video's " +
                "original hash. Neither changes a video. A download run stops at the daily limit or when the downloader refuses " +
                "and carries on where it stopped next time. Count and Download both save where they got to: one that was stopped or " +
                "cut off by an Emby restart carries on next time, with its totals so far; tick Start over to begin again. Also under " +
                "Scheduled Tasks → SubRemover, where it follows Test mode."),
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
                "dialogue like \"He was corrected by his teacher\" stays. Also an uploader's contact line anywhere in the file: one with " +
                "an e-mail address (name@site.com) or an international phone number (+ and 10 or more digits), which dialogue never " +
                "reads out in full."),
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
            (SettingsTabId.SubtitleFiles, "Fix subtitle file timing",
                "During runs, measures the .srt files of the videos the run checked against the speech in the video (the centre channel " +
                "of 5.1 and 7.1 audio, where films put the dialogue) and finds how far each file is off. A file that is off by the " +
                "same amount from start to end gets every cue moved by that amount; its text and cue lengths stay as they were. " +
                "It never stretches a file: one made for another frame rate or another cut, whose start, middle and end are off by " +
                "different amounts, is left alone and named in the log. Offsets under 0.3 s are left too. Reading a video's audio " +
                "takes a while, so a run only does it for videos with a subtitle file that's new or changed since it was last " +
                "checked. Each changed file is backed up first and shows on the restore list. OCR files are left alone."),
            (SettingsTabId.SubtitleFiles, "...delete an out-of-sync copy when another in the same language is in sync",
                "When a video has several regular subtitle files in one language, e.g. 'Movie.en.srt' and 'Movie.eng.srt', and one " +
                "of them is in sync, the ones that are off (or drift, or don't match the speech) are deleted, backed up first, rather " +
                "than moved. Files named or written as SDH, forced, commentary or audio description are never deleted this way: " +
                "they aren't copies of the regular one. When no copy is in sync, each one that can be is moved. Off: the off copy " +
                "is moved instead."),
            (SettingsTabId.SubtitleFiles, "...when none are in sync, keep the best copy and delete the worse ones",
                "An extended cut, director's cut or theatrical cut has scenes the other doesn't, and a PAL release runs faster than a " +
                "film one, so a subtitle made for another version lines up in one part of the video and not in another: its start, " +
                "middle and end are off by different amounts, and moving the whole file can't fix it. When a language has several " +
                "regular files and none are in sync, they're ranked: one that's off by one amount (it gets moved) beats one that " +
                "drifts, a smaller drift beats a bigger one, and any of those beats one that doesn't match the speech at all. With " +
                "this on, the best stays and the drifting or unmatched ones are deleted, backed up first so they're on the restore " +
                "list. The only file of a language is never deleted: a drift there is just logged, so you can look at it. SDH, " +
                "forced, commentary and audio-description files are never deleted this way. When two videos in one folder share " +
                "a name (Movie.mkv and Movie.mp4), they share their .srt files, which are then only measured, never moved or deleted."),
            (SettingsTabId.SubtitleFiles, "...delete subtitle files that are way off",
                "For a file that moving can't fix and that has no better copy to keep instead, so it's the only English file. Only " +
                "English .srt files (regular or SDH; never forced, commentary or audio description), and only when the video's " +
                "audio track is tagged English (or has no tag, with the setting below): the speech is what the file is measured " +
                "against. Careful deletes a file whose start, middle or end is more than 5 seconds off; Recommended does the same " +
                "at 2 seconds, so a file that's right for most of the video but jumps near the end goes too; Most also deletes files that " +
                "don't match the speech at all (a match score under 3, where 6 is a match), which can also be a quiet mix or a " +
                "music-heavy video, so check its Scan run first. Each file is backed up first and shows on the restore list. With " +
                "an English file gone, Emby may download a better one the next time it refreshes the video. Scan run lists what " +
                "would go; changing this setting doesn't need a new scan."),
            (SettingsTabId.SubtitleFiles, "...count audio with no language tag as English",
                "Many videos have an audio track with no language set (\"und\"), so the way-off setting skips them. Turn this on " +
                "when your untagged audio is English, and those videos are checked like English ones. Leave it off if you have " +
                "untagged videos in other languages. A plain Movie.srt with no language in its name still isn't counted as English."),
            (SettingsTabId.SubtitleFiles, "Scan run / Fix timing now",
                "Go through the .srt files next to the videos in your libraries (and path filter). Scan run measures every file and " +
                "changes nothing, whatever Test mode is set to: it lists each file and how far off it is in its log, and saves what " +
                "it found for each one (how far off, and what Fix would do) as a plan. Fix moves and deletes as described above, " +
                "backing each file up. It uses the plan for every video whose video and .srt files haven't changed since the scan, " +
                "without reading its audio again, and measures only the others; it also skips files it measured before that " +
                "haven't changed since (they were good, or it fixed them). Start over makes Fix ignore the plan and measure " +
                "everything. Both save where they got to: a run that's stopped, cut off by an Emby restart, " +
                "or paused by 'Videos per timing run' carries on from there next time, with its totals. Tick Start over to begin " +
                "again and measure every file. Neither changes a video, and neither runs next to a run, a restore or the ads task. " +
                "Also under Scheduled Tasks → SubRemover, where it follows Test mode."),
            (SettingsTabId.SubtitleFiles, "...skip .srt files extracted from the video's own subtitles",
                "When SubRemover extracts a built-in text subtitle to a file ('Extract kept text subtitles to .srt files'), it keeps a " +
                "list of the files it wrote. Those are exact copies of the video's own track, so they can't be out of sync: with " +
                "this on, they're taken as in sync without measuring, and a video whose only .srt files are such copies isn't read " +
                "at all, which saves most of the time. They still count as the in-sync copy of their language, so an off-sync " +
                "downloaded file next to one can still be deleted. Off: they're measured like any other file."),
            (SettingsTabId.SubtitleFiles, "Videos per timing run",
                "Reading a video's audio takes a while, so a whole library can take hours. Set a number to measure that many videos " +
                "per click (or scheduled run); the next one carries on where it stopped. 0 does them all in one go."),
            (SettingsTabId.SubtitleFiles, "Videos measured at once",
                "How many videos' audio is read side by side (each at low priority). 4 suits most servers; more is faster if the " +
                "server and the NAS keep up."),
            (SettingsTabId.SubtitleFiles, "Convert picture subtitles to text (OCR)",
                "Reads kept picture subtitles (PGS/DVD) with OCR and saves them as text next to the video, e.g. 'Movie.pgs.eng.srt'. The " +
                "picture track stays in the video. Skipped when the video also has a text subtitle in that language, and for commentary. " +
                "The language data (about 4 MB per language) is downloaded the first time. OCR can misread words."),
            (SettingsTabId.SubtitleFiles, "How OCR text is cleaned up",
                "Every OCR file goes through these fixes before it's saved. Timing (all languages): Blu-ray subtitles carry no end " +
                "time, so each line ends when the screen clears, and the last one gets a few seconds; the empty 'clear' pictures are " +
                "dropped; the same picture sent again a frame later becomes one subtitle. Two models (OCR model 'Both'): each line " +
                "keeps the reading with fewer words that look misread, checked against an English word list; a tie keeps the one " +
                "with more words. English only, each fix only where the misread can't be a real word: " +
                "a capital I read as 1, l, |, / or ! ('1 don't' → 'I don't', 'l think' → 'I think', '/t' → 'It' or 'it' mid-sentence); " +
                "'Is' in the middle of a sentence → 'is'; a capital K in 'Know' mid-sentence → 'know'; words with a 1 or | inside " +
                "get the l, I, t or i that makes a known word ('wi1l' → 'will'); missing apostrophes in common words ('dont' → " +
                "'don't'); 'So' read as '5o', 'S0', or '50' at the start of a sentence before a word a number can't come before " +
                "('50 what?' → 'So what?', while 'going over 50' stays); music notes read as *, », J\", f or ¢ → ♪; sound " +
                "descriptions with a misread bracket ('[ Door Opens /' → '[ Door Opens ]', '(Sighs]' → '[Sighs]'); double spaces. " +
                "Spot one it misses? Note the line and what it should say: new fixes are added to this list."),
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
                "Backups of .srt files that had ads taken out, were moved to fix their timing or were deleted as an out-of-sync copy ('Movie.en.srt.subremover.bak' next to it, or in the backup folder). " +
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
            (SettingsTabId.Errors, "Stop trying a video after this many failed runs",
                "Some videos fail every night the same way, for example when the rewritten file comes out a different length " +
                "and the original is kept. Each failed run counts one; once a video reaches this number, runs skip it and it no " +
                "longer takes up a place under 'Max videos to change per run'. A run that works for it, a retry, or a change to " +
                "the file (same name, new size or date) starts its count again. A run where 25 or more videos fail, and at least " +
                "half of the ones it looked at, is taken as a problem with the share, the disk or ffmpeg, so its failures don't " +
                "count. Test mode skips the same videos but never adds to the count. 0 never stops trying."),
            (SettingsTabId.Errors, "Videos that failed",
                "Every video that failed in a recent run: how many runs in a row, the reason the last time, and each attempt with " +
                "a link to that run's log. 'Retry now' forgets the failures and starts a run for just the ticked videos (no limit " +
                "per run); 'Retry on next run' only forgets them, so the next scheduled run tries them again. Neither works while " +
                "a SubRemover task is running. 'What's been going on' is subremover-failures.log in Emby's log folder: every " +
                "failure, skip and retry, newest first."),
            (SettingsTabId.Logs, "Logs",
                "Shows the last run. Pick an older run to see its log. The links under it open the whole log in a new window or " +
                "download it. The newest 30 are kept."),
        };

        /// <summary>The help entries for one tab, in tab order.</summary>
        internal static (SettingsTabId Tab, string Setting, string Text)[] For(SettingsTabId tab) => Entries.Where(e => e.Tab == tab).ToArray();

    }
}
