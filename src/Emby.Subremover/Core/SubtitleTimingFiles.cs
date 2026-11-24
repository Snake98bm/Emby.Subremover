using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Subremover.Core
{
    public enum TimingAction
    {
        /// <summary>In sync, or nothing to do.</summary>
        Keep,
        /// <summary>Move every cue by the measured offset.</summary>
        Shift,
        /// <summary>Out of sync while another regular file in the same language is in sync.</summary>
        Delete,
        /// <summary>Off, but moving can't fix it, or the match isn't clear: logged only.</summary>
        LeaveAlone
    }

    /// <summary>
    /// "Delete subtitle files that are way off": English subtitle files of videos with English audio that moving can't fix,
    /// deleted even when they're the only one. Each value's [Description] is its label on the settings page.
    /// </summary>
    public enum WayOffSubtitles
    {
        [Description("Off: never delete the only file of a language")]
        Off,
        [Description("Careful: drifts or jumps more than 5 s anywhere in the file")]
        MoreThan5Seconds,
        [Description("Recommended: drifts or jumps more than 2 s anywhere in the file")]
        MoreThan2Seconds,
        [Description("Most: as Recommended, plus files that don't match the speech at all")]
        MoreThan2SecondsOrNoMatch
    }

    /// <summary>One subtitle file of a video, measured against its speech.</summary>
    public sealed class MeasuredSubtitle
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>SubRemover extracted it from the video's own text track: an exact copy, in sync without measuring.</summary>
        public bool IsCopy { get; set; }

        /// <summary>"eng", or "" when the file name has no language.</summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>Why it isn't a regular subtitle ("SDH", "commentary", "forced"...), or null for a regular one.</summary>
        public string Special { get; set; }

        public int Cues { get; set; }
        public TimingResult Result { get; set; }
        public TimingAction Action { get; set; }

        /// <summary>For a deleted file: the in-sync file it's a duplicate of (null when it's deleted as another version's).</summary>
        public string KeptInstead { get; set; }

        /// <summary>The kept file was in sync (false: it was only the better copy).</summary>
        public bool KeptInSync { get; set; }

        /// <summary>Deleted by "Delete subtitle files that are way off", not for a better copy.</summary>
        public bool WayOff { get; set; }
    }

    /// <summary>
    /// "Fix subtitle file timing": measures the .srt files next to a video against the speech in its audio, and moves a
    /// file that is off by the same amount all the way through (never stretches it). When a video has several regular
    /// files in the same language (not SDH, forced, commentary or audio description) and one is in sync, the ones that
    /// are off are deleted instead: that's the copy the player should never pick. With "delete files made for another
    /// version", a file whose timing drifts or jumps (another cut, such as an extended or director's cut, or another frame
    /// rate) is deleted too, even when it's the only one. Each change is backed up first and shows on the restore list.
    /// </summary>
    public sealed class SubtitleTimingFiles
    {
        public const string ShiftNote = "Subtitle file: moved every cue {0} to match the speech";
        public const string DeleteNote = "Subtitle file: deleted, out of sync ({0}) while {1} is in sync";
        public const string BetterCopyNote = "Subtitle file: deleted, {0} (made for another version of the video) while {1} is a better copy";
        public const string WayOffNote = "Subtitle file: deleted, {0}; moving it can't fix that";

        /// <summary>"No clear match" files are only deleted as way off below this match score (6 is the bar for a match).</summary>
        public const double NoMatchBelow = 3.0;

        /// <summary>Raise when the measuring changes enough that files checked before should be measured again.</summary>
        public const int Version = 2;   // 2: start/middle/end all within 0.5 s counts as in sync, not drifting

        // File-name words for subtitles that aren't the plain dialogue: never deleted as a duplicate of a regular file.
        private static readonly Dictionary<string, string> SpecialWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sdh"] = "SDH", ["hi"] = "SDH", ["cc"] = "SDH", ["hoh"] = "SDH", ["deaf"] = "SDH",
            ["forced"] = "forced", ["foreign"] = "forced", ["signs"] = "forced", ["songs"] = "forced",
            ["commentary"] = "commentary", ["comment"] = "commentary", ["commentaries"] = "commentary", ["comm"] = "commentary",
            ["director"] = "commentary", ["directors"] = "commentary",
            ["ad"] = "audio description", ["desc"] = "audio description", ["described"] = "audio description",
            ["description"] = "audio description", ["descriptive"] = "audio description", ["dvs"] = "audio description",
            ["vi"] = "audio description", ["visual"] = "audio description", ["visually"] = "audio description",
            ["impaired"] = "SDH", ["narrative"] = "audio description", ["lyrics"] = "lyrics", ["karaoke"] = "lyrics"
        };

        private readonly FfmpegRunner _runner;
        private readonly bool _countOnly;
        private readonly bool _deleteOffDuplicates;
        private readonly bool _deleteOtherVersions;
        private readonly WayOffSubtitles _wayOff;
        private readonly BackupMode _backupMode;
        private readonly string _backupFolder;
        private readonly ILogger _log;

        public SubtitleTimingFiles(FfmpegRunner runner, bool countOnly, bool deleteOffDuplicates, bool deleteOtherVersions,
            BackupMode backupMode, string backupFolder, ILogger log, WayOffSubtitles wayOff = WayOffSubtitles.Off)
        {
            _wayOff = wayOff;
            _runner = runner;
            _countOnly = countOnly;
            _deleteOffDuplicates = deleteOffDuplicates;
            _deleteOtherVersions = deleteOtherVersions;
            _backupMode = backupMode;
            _backupFolder = backupFolder;
            _log = log;
        }

        public int VideosChecked;
        public int FilesChecked;
        public int Shifted;
        public int Deleted;
        public int WayOffDeleted;
        public int LeftAlone;
        public int Failed;

        /// <summary>"Skip copies of the built-in subtitles": files SubRemover extracted from the video's own text track are an
        /// exact copy of it, so they're in sync by definition and never measured (no audio is read for them).</summary>
        public bool SkipExtracted { get; set; }

        /// <summary>For the way-off setting: audio with no language tag (or "und") counts as English.</summary>
        public bool UntaggedAudioIsEnglish { get; set; }

        /// <summary>Extracted files left out this run because of <see cref="SkipExtracted"/>.</summary>
        public int SkippedCopies;

        private bool IsSkippedCopy(string path, SubremoverState state)
        {
            if (!SkipExtracted) return false;
            bool made;
            lock (state) made = state.MadeFileSet().Contains(path);
            if (made) Interlocked.Increment(ref SkippedCopies);
            return made;
        }

        private string BaseSignature => $"v{Version};min={SubtitleSync.DefaultMinShiftMs};delete={_deleteOffDuplicates};versions={_deleteOtherVersions}";

        // Any change here measures every file again: files left alone before may now be deleted.
        private string Signature => BaseSignature + (_wayOff == WayOffSubtitles.Off ? ""
            : $";wayoff={_wayOff};wayoffv={WayOffVersion}" + (UntaggedAudioIsEnglish ? ";untagged=eng" : ""));

        /// <summary>2: one part (start, middle or end) off by more than the limit is enough.</summary>
        private const int WayOffVersion = 2;

        /// <summary>The settings a saved <see cref="TimingPlan"/> must have been made with for Fix to use it. Not the way-off
        /// setting: Fix decides that again from the saved measurements.</summary>
        public string PlanSignature => BaseSignature + $";skipCopies={SkipExtracted}";

        /// <summary>
        /// Scan run: each measured video is recorded here. Fix: a video whose files haven't changed since is done from here,
        /// without reading its audio again; a changed one is measured again. Null: neither.
        /// </summary>
        public TimingPlan Plan { get; set; }

        /// <summary>Videos Fix did from the saved scan, and their files.</summary>
        public int FromPlan;
        public int FilesFromPlan;

        /// <summary>Videos in the saved scan that Fix measured again because the video or a file changed since.</summary>
        public int Rescanned;

        /// <summary>Call first: when the settings changed, every file is checked again.</summary>
        public void Begin(SubremoverState state)
        {
            if (_countOnly) return;
            lock (state)
            {
                if (state.TimingSignature == Signature) return;
                state.TimingChecked.Clear();
                state.TimingSignature = Signature;
            }
        }

        /// <summary>Whether the file is new, or changed since it was last checked.</summary>
        public bool NeedsCheck(string path, SubremoverState state)
        {
            if (_countOnly) return true;
            FileStamp stamp;
            lock (state)
            {
                if (state.TimingSignature != Signature || !state.TimingChecked.TryGetValue(path, out stamp)) return true;
            }
            return !stamp.Matches(MovedVideos.StampOrNull(path));
        }

        /// <summary>
        /// The video's .srt files (not OCR files, not ones put back from a backup on purpose). With
        /// <paramref name="onlyNewOrChanged"/>, a video whose files were all checked before is skipped without reading its
        /// audio.
        /// </summary>
        public async Task Check(string videoPath, SubremoverState state, bool onlyNewOrChanged, CancellationToken ct)
        {
            List<string> paths;
            try
            {
                paths = SubtitleAdFiles.FilesFor(videoPath)
                    .Where(p => !state.IsRestored(p) && !SubtitleAdFiles.IsOcrFile(p, videoPath, state))
                    .ToList();
            }
            catch (Exception ex)
            {
                _log.Debug($"SubRemover: couldn't list the subtitle files of {videoPath}: {ex.Message}");
                return;
            }
            if (paths.Count == 0)
            {
                if (_countOnly) Plan?.Remove(videoPath);
                return;
            }
            if (onlyNewOrChanged && !paths.Any(p => NeedsCheck(p, state))) return;

            // Fix: what the last Scan run found, when neither the video nor its files changed since.
            PlannedVideo planned = null;
            if (!_countOnly && Plan != null)
            {
                planned = Plan.Usable(videoPath, paths, MovedVideos.StampOrNull);
                // Scanned before the plan kept the audio language: the way-off setting needs it.
                if (planned != null && _wayOff != WayOffSubtitles.Off && planned.AudioLanguage == null) planned = null;
                if (planned == null && Plan.Get(videoPath) != null) Interlocked.Increment(ref Rescanned);
            }

            var subs = new List<(MeasuredSubtitle Sub, SrtAdCleaner.SrtFile File)>();
            // Each file as it was read, for the saved scan: a file changed after this is measured again by Fix.
            var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                try
                {
                    stamps[path] = MovedVideos.StampOrNull(path);
                    var file = SrtAdCleaner.Read(path);
                    var sub = Describe(path, videoPath, file.Text);
                    sub.Cues = SrtTimes.Read(file.Text).Count;
                    sub.IsCopy = IsSkippedCopy(path, state);
                    subs.Add((sub, file));
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref Failed);
                    _log.Error($"SubRemover: couldn't read {path}: {ex.Message}");
                }
            }
            // Nothing worth reading the audio for: too short to measure, or copies of the video's own track.
            if (!subs.Any(s => s.Sub.Cues >= SubtitleSync.MinCues && !s.Sub.IsCopy))
            {
                foreach (var s in subs) Note(s.Sub.Path, state);
                if (_countOnly) Plan?.Remove(videoPath);
                if (subs.Any(s => s.Sub.IsCopy))
                    _log.Debug($"SubRemover: subtitle timing of {videoPath}: only copies of its built-in subtitles ({string.Join(", ", subs.Where(s => s.Sub.IsCopy).Select(s => s.Sub.Name))}), in sync; not measured");
                return;
            }

            SpeechTrack speech = null;
            string audioLanguage = null;
            if (planned != null)
            {
                audioLanguage = planned.AudioLanguage;
                Interlocked.Increment(ref FromPlan);
                // Usable() only hands back a plan whose paths are distinct; the indexer never throws on a repeat anyway.
                var byPath = new Dictionary<string, PlannedFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in planned.Files.Where(f => f?.Path != null)) byPath[f.Path] = f;
                foreach (var (sub, _) in subs)
                {
                    var saved = byPath[sub.Path];
                    sub.Result = saved.Result();
                    sub.IsCopy = saved.IsCopy;
                    if (!sub.IsCopy) Interlocked.Increment(ref FilesFromPlan);
                }
            }
            else
            {
                Interlocked.Increment(ref VideosChecked);
                (speech, audioLanguage) = await ReadSpeech(videoPath, ct).ConfigureAwait(false);
                if (speech == null)
                {
                    Interlocked.Increment(ref Failed);
                    if (_countOnly) Plan?.Remove(videoPath);
                    return;
                }

                foreach (var (sub, file) in subs)
                {
                    // A copy of the built-in track counts as in sync, so an off copy in its language can still be deleted.
                    if (sub.IsCopy) { sub.Result = new TimingResult { Verdict = TimingVerdict.InSync, Confidence = 99 }; continue; }
                    Interlocked.Increment(ref FilesChecked);
                    sub.Result = SubtitleSync.Measure(speech, SrtTimes.Read(file.Text));
                }
            }
            Decide(subs.Select(s => s.Sub).ToList(), _deleteOffDuplicates, _deleteOtherVersions, _wayOff, audioLanguage, UntaggedAudioIsEnglish);

            // "Movie.mkv" and "Movie.mp4" in one folder share their .srt files, and may be different cuts: what's right
            // for one can be wrong for the other, so nothing is moved or deleted; the log says what one of them found.
            var sharedWith = SameNameVideos(videoPath);
            var lines = new List<string>();
            if (sharedWith.Count > 0)
            {
                foreach (var (sub, _) in subs.Where(s => s.Sub.Action == TimingAction.Shift || s.Sub.Action == TimingAction.Delete))
                    sub.Action = TimingAction.LeaveAlone;
                lines.Add($"(its .srt files are shared with {string.Join(", ", sharedWith.Select(Path.GetFileName))}, so they're only measured, never moved or deleted)");
            }
            foreach (var (sub, file) in subs)
            {
                var kind = sub.Special != null ? $" ({sub.Special})" : "";
                switch (sub.Action)
                {
                    case TimingAction.Shift:
                        lines.Add($"{sub.Name}{kind}: {sub.Result.Describe()}" + (_countOnly ? Would(ref Shifted, " - not moved: scan run") : Shift(sub, file, state)));
                        break;
                    case TimingAction.Delete:
                        // Way-off deletions are counted once done (Delete), or here for a scan run.
                        if (sub.WayOff && _countOnly) Interlocked.Increment(ref WayOffDeleted);
                        var why = sub.WayOff ? "way off, and moving it can't fix that"
                            : sub.KeptInstead + (sub.KeptInSync ? " is in sync" : " is a better copy");
                        lines.Add($"{sub.Name}{kind}: {sub.Result.Describe()}; deleted: {why}" +
                                  (_countOnly ? Would(ref Deleted, " - not deleted: scan run") : Delete(sub, state)));
                        break;
                    case TimingAction.LeaveAlone:
                        Interlocked.Increment(ref LeftAlone);
                        lines.Add($"{sub.Name}{kind}: {sub.Result.Describe()}");
                        Note(sub.Path, state);
                        break;
                    default:
                        lines.Add($"{sub.Name}{kind}: " + (sub.IsCopy ? "copy of the built-in subtitle, in sync (not measured)"
                            : sub.Result == null || sub.Cues < SubtitleSync.MinCues ? $"only {sub.Cues} cues, not measured" : sub.Result.Describe()));
                        Note(sub.Path, state);
                        break;
                }
            }
            if (_countOnly && Plan != null)
                Plan.Put(new PlannedVideo
                {
                    VideoPath = videoPath,
                    VideoStamp = MovedVideos.StampOrNull(videoPath),
                    ScannedUtc = DateTime.UtcNow,
                    AudioMinutes = Math.Round(speech.Seconds / 60, 1),
                    AudioLanguage = audioLanguage ?? "",
                    Files = subs.Select(s => PlannedFile.From(s.Sub, stamps.TryGetValue(s.Sub.Path, out var st) ? st : null)).ToList()
                });
            // Done (or measured again): the saved scan of this video no longer says what's there.
            if (!_countOnly) Plan?.Remove(videoPath);

            var anything = subs.Any(s => s.Sub.Action != TimingAction.Keep);
            var source = speech != null ? $"{speech.Seconds / 60:0} min of audio"
                : $"from the scan run of {planned.ScannedUtc.ToLocalTime():d MMM HH:mm}, not measured again";
            var text = $"SubRemover: subtitle timing of {videoPath} ({source}):" + string.Concat(lines.Select(l => "\n    " + l));
            if (anything) _log.Info(text);
            else _log.Debug(text);
        }

        /// <summary>
        /// What to do with each file of one video, from their measurements. A file is never deleted for its own timing
        /// alone: only when another regular file in the same language is better, so every language keeps its best file.
        /// </summary>
        /// <param name="deleteOffDuplicates">When a language has a file in sync, delete its files that aren't.</param>
        /// <param name="deleteOtherVersions">When no file of a language is in sync, keep the best one and delete the ones
        /// that drift or don't match the speech (made for another cut or frame rate).</param>
        /// <param name="wayOff">Also delete English files that are way off when the audio is English, even the only one.</param>
        /// <param name="audioLanguage">The language tag of the audio track that was measured ("eng"), or null/"" when unknown.</param>
        /// <param name="untaggedAudioIsEnglish">Audio with no language tag (null, "" or "und") counts as English.</param>
        public static void Decide(IList<MeasuredSubtitle> subs, bool deleteOffDuplicates, bool deleteOtherVersions = false,
            WayOffSubtitles wayOff = WayOffSubtitles.Off, string audioLanguage = null, bool untaggedAudioIsEnglish = false)
        {
            DecideCopies(subs, deleteOffDuplicates, deleteOtherVersions);
            if (wayOff == WayOffSubtitles.Off || !AudioIsEnglish(audioLanguage, untaggedAudioIsEnglish)) return;
            foreach (var sub in subs.Where(s => s.Action == TimingAction.LeaveAlone && IsWayOff(s, wayOff)))
            {
                sub.Action = TimingAction.Delete;
                sub.WayOff = true;
            }
        }

        /// <summary>Whether the measured audio is English: tagged so, or untagged when that's asked to count.</summary>
        internal static bool AudioIsEnglish(string audioLanguage, bool untaggedAudioIsEnglish)
        {
            var lang = Languages.Normalize(audioLanguage);
            return lang == "eng" || (untaggedAudioIsEnglish && lang == "und");
        }

        /// <summary>
        /// An English file (regular or SDH, never forced, commentary or audio description) that moving can't fix: its start,
        /// middle or end is off by more than the limit (a jump near the end counts too); or, with the most setting, it
        /// doesn't match the speech.
        /// </summary>
        internal static bool IsWayOff(MeasuredSubtitle s, WayOffSubtitles wayOff)
        {
            var r = s.Result;
            if (r == null || s.IsCopy || s.Cues < SubtitleSync.MinCues) return false;
            if (s.Language != "eng" || (s.Special != null && s.Special != "SDH")) return false;
            if (r.Verdict == TimingVerdict.Drifts)
            {
                var limit = wayOff == WayOffSubtitles.MoreThan5Seconds ? 5000 : 2000;
                return (r.PartOffsetsMs ?? new long[0]).Any(o => Math.Abs(o) > limit);
            }
            // Confidence 0 means it couldn't be worked out at all (too little audio): never a reason to delete.
            return r.Verdict == TimingVerdict.Unsure && wayOff == WayOffSubtitles.MoreThan2SecondsOrNoMatch
                && r.Confidence > 0 && r.Confidence < NoMatchBelow;
        }

        private static void DecideCopies(IList<MeasuredSubtitle> subs, bool deleteOffDuplicates, bool deleteOtherVersions)
        {
            foreach (var sub in subs)
            {
                var r = sub.Result;
                if (r == null || sub.Cues < SubtitleSync.MinCues) sub.Action = TimingAction.Keep;
                else if (r.Verdict == TimingVerdict.Shift) sub.Action = TimingAction.Shift;
                else if (r.Verdict == TimingVerdict.InSync) sub.Action = TimingAction.Keep;
                // Drifts or no clear match: moving can't fix it, so it's only logged, unless a better copy is there (below).
                else sub.Action = TimingAction.LeaveAlone;
            }
            if (!deleteOffDuplicates && !deleteOtherVersions) return;

            // Regular files only: SDH, forced, commentary and audio description aren't copies of each other.
            foreach (var group in subs.Where(s => s.Special == null && s.Result != null && s.Cues >= SubtitleSync.MinCues)
                         .GroupBy(s => s.Language, StringComparer.OrdinalIgnoreCase))
            {
                var ranked = group.OrderByDescending(Rank).ThenBy(Spread).ThenByDescending(s => s.Result.Confidence).ToList();
                if (ranked.Count < 2) continue;   // the only file of its language always stays
                var best = ranked[0];
                var bestRank = Rank(best);

                foreach (var other in ranked.Skip(1))
                {
                    var verdict = other.Result.Verdict;
                    bool delete;
                    if (best.Result.Verdict == TimingVerdict.InSync)
                        // A file in sync: every copy that isn't goes (if asked); another file in sync stays.
                        delete = deleteOffDuplicates && verdict != TimingVerdict.InSync;
                    else
                        // None in sync: keep the best (moved if it's off by a fixed amount) and the other movable ones;
                        // the ones that drift or don't match the speech go, if asked.
                        // A file that couldn't be measured at all (confidence 0, e.g. silent audio) is never a reason to
                        // delete, and an unmeasurable best copy says nothing about the others.
                        delete = deleteOtherVersions && best.Result.Confidence > 0
                                 && (verdict == TimingVerdict.Drifts || (verdict == TimingVerdict.Unsure && other.Result.Confidence > 0))
                                 && Rank(other) <= bestRank;
                    if (!delete) continue;
                    other.Action = TimingAction.Delete;
                    other.KeptInstead = best.Name;
                    other.KeptInSync = best.Result.Verdict == TimingVerdict.InSync;
                }
            }
        }

        /// <summary>Better first: in sync, then off by one amount (it can be moved), then drifting, then no clear match.</summary>
        internal static int Rank(MeasuredSubtitle s)
        {
            switch (s.Result?.Verdict)
            {
                case TimingVerdict.InSync: return 4;
                case TimingVerdict.Shift: return 3;
                case TimingVerdict.Drifts: return 2;
                default: return 1;
            }
        }

        // Among drifting files, the one whose start, middle and end are closest together is the better copy.
        private static long Spread(MeasuredSubtitle s) =>
            s.Result?.PartOffsetsMs != null && s.Result.PartOffsetsMs.Length > 1 ? s.Result.PartOffsetsMs.Max() - s.Result.PartOffsetsMs.Min() : 0;

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".wmv", ".mpg", ".mpeg", ".webm", ".flv", ".iso", ".vob", ".divx", ".ogm"
        };

        /// <summary>Whether the file name has a video extension (any case).</summary>
        public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));

        /// <summary>Other video files in the folder with the same name ("Movie.mp4" next to "Movie.mkv"): they share the .srt files.</summary>
        public static List<string> SameNameVideos(string videoPath)
        {
            try
            {
                var dir = Path.GetDirectoryName(videoPath);
                var stem = Path.GetFileNameWithoutExtension(videoPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new List<string>();
                // Every file, filtered here: a search pattern is case-sensitive on Linux.
                return Directory.EnumerateFiles(dir)
                    .Where(f => !string.Equals(f, videoPath, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                                && VideoExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>The language and kind of a file, from its name ("Movie.en.sdh.srt") and, for SDH, its text.</summary>
        public static MeasuredSubtitle Describe(string path, string videoPath, string text)
        {
            var sub = new MeasuredSubtitle { Path = path };
            var stem = Path.GetFileNameWithoutExtension(path);
            var name = Path.GetFileNameWithoutExtension(videoPath);
            var tokens = stem.Length > name.Length
                ? stem.Substring(name.Length).Split(new[] { '.', ' ', '_', '-', '[', ']', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                : new string[0];
            foreach (var token in tokens)
            {
                if (sub.Special == null && SpecialWords.TryGetValue(token, out var special)) sub.Special = special;
                else if (sub.Language.Length == 0 && !SpecialWords.ContainsKey(token)) sub.Language = LanguageOf(token);
            }
            if (sub.Special == null && SdhDetector.Looks(text, out _)) sub.Special = "SDH";
            return sub;
        }

        private static string Would(ref int counter, string text)
        {
            Interlocked.Increment(ref counter);
            return text;
        }

        // "en", "eng" and "English" alike; "" for anything else.
        private static string LanguageOf(string token)
        {
            if (Languages.IsKnown(token)) return Languages.Normalize(token);
            return Languages.All.FirstOrDefault(l => string.Equals(l.Name, token, StringComparison.OrdinalIgnoreCase))?.Code ?? string.Empty;
        }

        private string Shift(MeasuredSubtitle sub, SrtAdCleaner.SrtFile file, SubremoverState state)
        {
            try
            {
                var temp = sub.Path + Leftovers.TempSuffix;
                File.WriteAllBytes(temp, SrtAdCleaner.Encode(file, SrtTimes.Shift(file.Text, sub.Result.OffsetMs)));
                var replaced = BackupManager.Replace(sub.Path, temp, _backupMode, _backupFolder);
                if (!replaced.Success)
                {
                    Interlocked.Increment(ref Failed);
                    return " - couldn't change it: " + replaced.Message;
                }
                SubtitleAdFiles.RecordBackup(state, sub.Path, replaced, string.Format(ShiftNote, SrtTimes.Describe(sub.Result.OffsetMs)));
                Interlocked.Increment(ref Shifted);
                Note(sub.Path, state);
                return replaced.BackupPath != null ? $" (backup: {replaced.BackupPath})" : " (no backup kept)";
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Failed);
                return " - couldn't change it: " + ex.Message;
            }
        }

        private string Delete(MeasuredSubtitle sub, SubremoverState state)
        {
            var removed = BackupManager.Remove(sub.Path, _backupMode, _backupFolder);
            if (!removed.Success)
            {
                Interlocked.Increment(ref Failed);
                return " - couldn't delete it: " + removed.Message;
            }
            var off = sub.Result.Verdict == TimingVerdict.Drifts ? "drifts" : sub.Result.Verdict == TimingVerdict.Shift
                ? "off by " + SrtTimes.Describe(-sub.Result.OffsetMs) : "doesn't match the speech";
            SubtitleAdFiles.RecordBackup(state, sub.Path, removed,
                sub.WayOff ? string.Format(WayOffNote, sub.Result.Needs())
                : sub.KeptInSync ? string.Format(DeleteNote, off, sub.KeptInstead) : string.Format(BetterCopyNote, off, sub.KeptInstead));
            Interlocked.Increment(ref Deleted);
            if (sub.WayOff) Interlocked.Increment(ref WayOffDeleted);
            lock (state) state.TimingChecked.Remove(sub.Path);
            return removed.BackupPath != null ? $" (backup: {removed.BackupPath})" : " (no backup kept)";
        }

        private void Note(string path, SubremoverState state)
        {
            if (_countOnly) return;
            var stamp = MovedVideos.StampOrNull(path);
            if (stamp == null) return;
            lock (state) state.TimingChecked[path] = stamp;
        }

        /// <summary>
        /// The dialogue of the video's main audio track as a <see cref="SpeechTrack"/>: the centre channel of 5.1 and 7.1
        /// audio (where films put the dialogue), else everything mixed to mono; 200 Hz - 3.5 kHz, 8 kHz.
        /// </summary>
        private async Task<(SpeechTrack Speech, string Language)> ReadSpeech(string videoPath, CancellationToken ct)
        {
            var probe = await _runner.RunFfprobe(new List<string>
            {
                "-v", "error", "-select_streams", "a", "-show_entries", "stream=index,channels:stream_disposition=default:stream_tags=language",
                "-of", "csv=p=0", videoPath
            }, ct).ConfigureAwait(false);
            if (!probe.Success)
            {
                _log.Error($"SubRemover: ffprobe failed for {videoPath} (exit {probe.ExitCode}): {probe.StdErr}");
                return (null, null);
            }
            var streams = probe.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split(','))
                .Where(p => p.Length >= 2 && int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .Select(p => (Index: int.Parse(p[0], CultureInfo.InvariantCulture),
                    Channels: int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 2,
                    Default: p.Length > 2 && p[2].Trim() == "1",
                    Language: p.Length > 3 ? p[3].Trim() : ""))
                .ToList();
            if (streams.Count == 0)
            {
                _log.Info($"SubRemover: {videoPath} has no audio; its subtitle timing can't be checked");
                return (null, null);
            }
            var audio = streams.FirstOrDefault(s => s.Default);
            if (!streams.Any(s => s.Default)) audio = streams[0];

            var filter = (audio.Channels >= 6 ? "pan=mono|c0=c2," : "") + "highpass=f=200,lowpass=f=3500";
            var args = new List<string>
            {
                "-hide_banner", "-nostdin", "-v", "error", "-i", videoPath, "-map", "0:" + audio.Index.ToString(CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn", "-af", filter, "-ac", "1", "-ar", SpeechTrack.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "s16le", "pipe:1"
            };
            var (result, track) = await _runner.RunFfmpegBinary(args, SpeechTrack.FromPcm, ct).ConfigureAwait(false);
            if (!result.Success || track.Level.Length == 0)
            {
                _log.Error($"SubRemover: couldn't read the audio of {videoPath} (exit {result.ExitCode}): {result.StdErr}");
                return (null, null);
            }
            return (track, Languages.Normalize(audio.Language));
        }

        /// <summary>"3 subtitle files moved, 1 deleted..." for the logs and the settings page.</summary>
        public string Describe() =>
            (_countOnly
                ? $"{FilesChecked} subtitle files of {VideosChecked} videos checked: {Shifted} would be moved, {Deleted} deleted, {LeftAlone} left alone (need more than a shift); nothing changed"
                : $"{FilesChecked + FilesFromPlan} subtitle files of {VideosChecked + FromPlan} videos checked: {Shifted} moved, {Deleted} deleted, {LeftAlone} left alone (need more than a shift)") +
            (WayOffDeleted > 0 ? $", {WayOffDeleted} of the deleted {(_countOnly ? "would go" : "went")} for being way off (English)" : "") +
            (FromPlan > 0 ? $", {FromPlan} videos done from the scan run without measuring again" : "") +
            (Rescanned > 0 ? $", {Rescanned} measured again because the video or a subtitle file changed since the scan" : "") +
            (SkippedCopies > 0 ? $", {SkippedCopies} copies of built-in subtitles taken as in sync without measuring" : "") +
            (Failed > 0 ? $", {Failed} couldn't be read or changed" : "");
    }
}
