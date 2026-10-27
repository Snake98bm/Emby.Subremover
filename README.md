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
- Optional: remove SDH/hearing-impaired tracks, keep forced tracks in any language, keep tracks with no language set.

## Install

1. Download `Emby.Subremover.dll` from the [Releases](../../releases) page, or build it yourself (below).
2. Stop Emby Server and copy the DLL into Emby's `plugins` folder (e.g. `C:\Emby\programdata\plugins`).
3. Start Emby. Go to **Settings → Plugins → Subremover**, pick your libraries and languages, and save.
4. Go to **Settings → Scheduled Tasks → Subremover → Remove unwanted subtitles** and run it.
5. With Test mode on, open **Settings → Logs** and read the `Subremover` lines. When they look right, turn Test mode off and run it again.

## Build it yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then, in PowerShell from this folder:

```powershell
.\build.ps1            # build + run tests + make artifacts\Emby.Subremover-<version>.zip
.\build.ps1 test       # just run the tests
.\build.ps1 deploy -PluginsFolder '\\YOUR-EMBY-PC\C$\Emby\programdata\plugins'
```

`deploy` builds, runs the tests, and copies the plugin into Emby. It refuses to deploy if any test fails, and it first saves the installed DLL to a `subremover-backups` folder so you can go back.

### Tests with Emby's own ffmpeg

Most tests need nothing extra. Three tests run real ffmpeg on a tiny generated video. To enable them, copy `ffmpeg.exe`, `ffprobe.exe` and their DLLs from your Emby server's `system` folder into `tools\ffmpeg\` (git ignores that folder). Without them, those tests show as *skipped*.

---

## Git and GitHub, the short version

**Git** keeps the full history of every change to this folder on your PC. **GitHub** is the online copy of that history, which also serves as an off-site backup. Each saved change is a **commit**: a snapshot plus a message saying what changed and why.

### See what changed

| Command | What it shows |
|---|---|
| `git status` | Which files have changed since the last commit |
| `git diff` | The exact lines changed (red = removed, green = added) |
| `git log --oneline` | The list of commits, newest first |
| `git show <commit>` | What one commit changed |

On GitHub, the **Commits** link on the repo page shows the same history with colored diffs. That's the easiest way to review what Claude changed.

### The everyday loop

```powershell
git pull                      # get the latest from GitHub first
# ...make changes (or ask Claude to)...
.\build.ps1                   # build + test
git add -A                    # stage the changes
git commit -m "Explain what and why"
git push                      # upload to GitHub
```

### Trying something risky? Use a branch

A branch is a separate line of work. `main` stays safe while you experiment.

```powershell
git switch -c try-new-idea    # create the branch and switch to it
# ...commit as usual...
git push -u origin try-new-idea
gh pr create --fill           # open a pull request to review the changes on GitHub
```

If you like it, merge the pull request on GitHub. If not, close it and switch back with `git switch main`. Nothing on `main` was touched.

### Undo

| Situation | Command |
|---|---|
| Throw away uncommitted edits to one file | `git restore <file>` |
| Undo a commit that's already pushed (safe) | `git revert <commit>`, then `git push` |
| See an old version of a file | `git show <commit>:path/to/file` |

### Releases (a backup of each built version)

When a version works on your server, tag it:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions then builds and tests it, and publishes a **Release** with the DLL attached. Every version you've ever shipped stays downloadable there. Every push also gets an automatic build and test run, under the **Actions** tab on GitHub.

## License

MIT. See [LICENSE](LICENSE).
