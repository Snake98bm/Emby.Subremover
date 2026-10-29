# Emby.Subremover

An Emby Server plugin that removes unwanted **embedded subtitle tracks** from your videos. You choose which languages to keep, and every other subtitle track is stripped out. Video and audio are copied untouched (no re-encoding).

It was inspired by the old *SubKiller* plugin, which no longer works on current Emby. This is a fresh rewrite against the Emby 4.9/4.10 plugin API.

## Features

- **Test mode (on by default)** – logs exactly what *would* change and touches nothing.
- **Safe replace** – writes a new file, then checks it has the same video and audio tracks, the expected subtitle tracks and the same length. If any check fails, the original stays untouched.
- **Backups** – the original is kept as `movie.mkv.subremover.bak` next to the video, or copied to a backup folder you choose. Old backups can be deleted automatically after a number of days.
- **Undo** – the **Restore originals from backups** button on the settings page (or the task of the same name) puts the original files back for the videos matching *Only process paths containing*. Restoring everything needs its own tick.
- **Test on a few files first** – *Max videos per run* and *Only process paths containing* let you try it on a single movie. Separate several titles with `;`, e.g. `My Movie (2020); My Show - S01E02`.
- Optional: extract kept text subtitles to `.srt` files that Emby picks up as external subtitles.
- Experimental: OCR picture subtitles (PGS/DVD) into text files such as `Movie.pgs.eng.srt`, using the OCR built into Emby's own ffmpeg. The picture track stays in the video, and you choose whether an existing OCR file is kept or replaced. Tracks that also exist as text in the same language aren't OCR'd; extract the text one instead. Tesseract language data (about 4 MB per language) is downloaded the first time it's needed.
- Default subtitle: when a video marks an SDH, CC or commentary track as the default, the flag moves to the regular track in the same language. Or choose a default subtitle language, and its regular track becomes the default whatever the video says. Optionally, the extracted or OCR file of the regular track is named `Movie.eng.default.srt` so Emby turns that one on.
- Optional: offer OCR in Emby's subtitle search. Search for subtitles on a video and pick "OCR of the built-in subtitles" to turn its picture track into a text file on demand.
- Optional: guess the language of text subtitles that have none set (from common words in the first 15 minutes, or the writing system). A guessed track is kept or removed like any other, and a kept one gets the language written into the video. Tracks that share a language are read too, to catch wrong language tags and untitled SDH copies. Or use the guesses only to name extracted files, leaving the video as it is.
- Optional: remove commentary tracks, remove SDH/CC/hearing-impaired tracks (only when a regular track in the same language stays), keep forced tracks in any language, keep tracks with no language set.
- Its own settings page with **Run now**, run status and a log viewer, plus an entry in the server sidebar under **Advanced** (can be turned off).

## Install

1. Download `Emby.Subremover.dll` from the [Releases](../../releases) page, or build it yourself (below).
2. Stop Emby Server and copy the DLL into Emby's `plugins` folder. The default locations are:

   | System | Plugins folder |
   |---|---|
   | Windows | `%AppData%\Emby-Server\programdata\plugins` |
   | Linux | `/var/lib/emby/plugins` |
   | Docker (emby/embyserver, linuxserver/emby) | `/config/plugins` inside the container |

   If you installed Emby somewhere else, it's the `plugins` folder inside Emby's data folder (the one with `config`, `data` and `logs`).

   **Docker:** on the host, that's the `plugins` folder inside the folder you mapped to `/config`, e.g. `/docker/emby/config/plugins`. Stop the container first (`docker stop emby`), and give the DLL the same owner as the other files in that folder (your PUID/PGID). Or copy it straight into the container, using your container's name:

   ```bash
   docker cp Emby.Subremover.dll emby:/config/plugins/
   ```
3. Start Emby (Docker: `docker start emby`) and press **Ctrl+F5** in your browser so the menus reload.
4. Go to **Settings → Plugins → SubRemover** (or **SubRemover** under **Advanced** in the server sidebar). Pick your libraries and languages, or click **Quick setup: English + no language**, and leave **Test mode** on.
5. Click **Run now**. The status under the button shows progress, and the run log appears at the bottom of the page.
6. When the log looks right, turn Test mode off, set **Max videos to change per run** to a few, and run it again. Check those files in Emby before letting it do the rest.

The task is also under **Settings → Scheduled Tasks → SubRemover**, where you can give it a schedule. Each run writes its own `subremover-….txt` log in Emby's log folder; the newest 30 are kept.

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then, in PowerShell from this folder:

```powershell
.\build.ps1            # build + run tests + make artifacts\Emby.Subremover-<version>.zip
.\build.ps1 test       # just run the tests
.\build.ps1 deploy -PluginsFolder "$env:APPDATA\Emby-Server\programdata\plugins"
```

`deploy` builds, runs the tests, and copies the plugin into Emby. It refuses to deploy if any test fails, and it first saves the installed DLL to a `subremover-backups` folder so you can go back.

### Tests with Emby's own ffmpeg

Most tests need nothing extra. Three tests run real ffmpeg on a tiny generated video. To enable them, copy `ffmpeg.exe`, `ffprobe.exe` and their DLLs from your Emby server's `system` folder into `tools\ffmpeg\` (git ignores that folder). Without them, those tests show as *skipped*.
