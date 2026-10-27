# Emby.Subremover

An Emby Server plugin that removes unwanted **embedded subtitle tracks** from your videos. You choose which languages to keep, and every other subtitle track is stripped out. Video and audio are copied untouched (no re-encoding).

It was inspired by the old *SubKiller* plugin, which no longer works on current Emby. This is a fresh rewrite against the Emby 4.9/4.10 plugin API.

## Features

- **Test mode (on by default)** – logs exactly what *would* change and touches nothing.
- **Safe replace** – writes a new file, then checks it has the same video and audio tracks, the expected subtitle tracks and the same length. If any check fails, the original stays untouched.
- **Backups** – the original is kept as `movie.mkv.subremover.bak` next to the video, or copied to a backup folder you choose. Old backups can be deleted automatically after a number of days.
- **Undo** – the *Restore originals from backups* task puts the original files back (everything, or one title).
- **Test on a few files first** – *Max videos per run* and *Only process paths containing* let you try it on a single movie.
- Optional: extract kept text subtitles to `.srt` files that Emby picks up as external subtitles.
- Experimental: OCR picture subtitles (PGS/DVD) into text files such as `Movie.pgs.eng.srt`, using the OCR built into Emby's own ffmpeg. The picture track stays in the video, and you choose whether an existing OCR file is kept or replaced. Tesseract language data (about 4 MB per language) is downloaded the first time it's needed.
- Optional: remove commentary tracks, remove SDH/hearing-impaired tracks, keep forced tracks in any language, keep tracks with no language set.

## Install

1. Download `Emby.Subremover.dll` from the [Releases](../../releases) page, or build it yourself (below).
2. Stop Emby Server and copy the DLL into Emby's `plugins` folder. The default locations are:

   | System | Plugins folder |
   |---|---|
   | Windows | `%AppData%\Emby-Server\programdata\plugins` |
   | Linux | `/var/lib/emby/plugins` |

   If you installed Emby somewhere else, it's the `plugins` folder inside Emby's data folder (the one with `config`, `data` and `logs`).
3. Start Emby. Go to **Settings → Plugins → Subremover**, pick your libraries and languages, and save.
4. Go to **Settings → Scheduled Tasks → Subremover → Remove unwanted subtitles** and run it.
5. With Test mode on, open **Settings → Logs** and read the `Subremover` lines. When they look right, turn Test mode off and run it again.

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
