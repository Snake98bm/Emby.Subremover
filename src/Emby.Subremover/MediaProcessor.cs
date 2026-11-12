using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover
{
    public enum Outcome
    {
        AlreadyDone,
        NothingToDo,
        WouldChange,
        Changed,
        Failed
    }

    public class MediaProcessor
    {
        private const string TempSuffix = Leftovers.TempSuffix;

        private readonly FfmpegRunner _runner;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _json;
        private readonly ILogger _log;
        private readonly Tessdata _tessdata;
        private readonly SearchOcrStore _searchOcr;
        private readonly HashMatchedSubtitles _hashSubtitles;

        /// <summary>What this processor did (or would do in test mode) so far; one processor is made per run.</summary>
        public RunTotals Totals { get; } = new RunTotals();

        public MediaProcessor(FfmpegRunner runner, ILibraryMonitor libraryMonitor, IProviderManager providerManager,
            IFileSystem fileSystem, IJsonSerializer json, ILogger log, Tessdata tessdata, SearchOcrStore searchOcr = null,
            Func<ILogger, HashMatchedSubtitles> hashSubtitles = null)
        {
            _hashSubtitles = hashSubtitles?.Invoke(log);
            _tessdata = tessdata;
            _searchOcr = searchOcr;
            _runner = runner;
            _libraryMonitor = libraryMonitor;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _json = json;
            _log = log;
        }

        public static PlannerSettings SettingsFrom(PluginOptions o) => new PlannerSettings
        {
            KeepLanguages = PlannerSettings.ParseLanguages(o.KeepLanguages),
            KeepUndefined = o.KeepUndefined,
            KeepForcedAnyLanguage = o.KeepForcedAnyLanguage,
            RemoveHearingImpaired = o.RemoveHearingImpaired,
            RemoveCommentary = o.RemoveCommentary,
            ExtractTextSubtitles = o.ExtractTextSubtitles,
            RemoveExtractedFromVideo = o.ExtractTextSubtitles && o.RemoveExtractedFromVideo,
            OcrPictureSubtitles = o.OcrPictureSubtitles,
            OcrReplaceExisting = o.OcrExistingFile == ExistingOcrFile.Replace,
            OcrRedoOlder = o.OcrExistingFile == ExistingOcrFile.RedoIfMadeByAnOlderOcr ? OcrVersion.Current + "/" + o.OcrModel : "",
            MakeRegularDefault = o.MakeRegularDefault,
            MarkDefaultInName = o.MarkDefaultInName,
            GuessLanguages = o.GuessLanguages,
            GuessForFilesOnly = o.GuessLanguages && o.GuessForFilesOnly,
            FixWrongTags = o.WrongLanguageTags == WrongLanguageTags.FixTheTag,
            RemoveUnknownWrongTags = o.WrongLanguageTags == WrongLanguageTags.FixTheTag && o.RemoveWrongTagsLeftUndefined,
            CheckEveryTextSubtitle = o.WrongLanguageTags == WrongLanguageTags.FixTheTag && o.CheckEveryTextSubtitle,
            PreferExistingFiles = o.PreferExistingFiles,
            DefaultLanguage = string.IsNullOrWhiteSpace(o.DefaultLanguage) ? null : Languages.Normalize(o.DefaultLanguage)
        };

        /// <summary>Checks and, when needed, changes one video.</summary>
        public async Task<Outcome> Process(BaseItem item, PluginOptions options, SubremoverState state, StateStore store, CancellationToken ct)
        {
            var video = await Check(item, options, state, store, ct).ConfigureAwait(false);
            return await Change(video, options, state, store, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads a video and decides what to do with it. Several videos may be checked at once: it only reads the shared
        /// state, under its lock (a video with nothing to do is noted as done).
        /// </summary>
        public async Task<Checked> Check(BaseItem item, PluginOptions options, SubremoverState state, StateStore store, CancellationToken ct)
        {
            var path = item.Path;
            bool restored;
            lock (state) restored = state.IsRestored(path);
            if (restored)
            {
                _log.Debug("SubRemover: {0} was restored from its backup, leaving it alone", path);
                return Checked.Finished(Outcome.AlreadyDone);
            }

            var stamp = FileStamp.Of(path);
            bool alreadyDone;
            lock (state) alreadyDone = state.Processed.TryGetValue(path, out var done) && done.Matches(stamp);
            if (alreadyDone) return Checked.Finished(Outcome.AlreadyDone);

            var before = await Probe(path, ct).ConfigureAwait(false);
            if (before == null) return Checked.Finished(Outcome.Failed);

            // A guess only for file names is no use when nothing is extracted, and reading the video costs time.
            var settings = SettingsFrom(options);
            var rules = new GuessRules
            {
                GuessUntagged = options.GuessLanguages && (!options.GuessForFilesOnly || options.ExtractTextSubtitles),
                UntaggedFilesOnly = options.GuessForFilesOnly,
                FixWrongTags = settings.FixWrongTags,
                RemoveUnknownWrongTags = settings.RemoveUnknownWrongTags,
                CheckEveryTextSubtitle = settings.CheckEveryTextSubtitle
            };
            if (rules.GuessUntagged || rules.FixWrongTags)
                await GuessLanguages(path, before, settings, rules, ct).ConfigureAwait(false);

            var redoOlder = options.OcrExistingFile == ExistingOcrFile.RedoIfMadeByAnOlderOcr && _searchOcr != null;
            var searchRecords = redoOlder ? _searchOcr.Load() : new System.Collections.Generic.List<SearchOcrRecord>();
            // Current if a run made it with today's OCR, or the subtitle search saved today's OCR into it.
            bool OcrFileIsCurrent(string f) =>
                state.OcrFileIsCurrent(f, options.OcrModel)
                || searchRecords.Any(r => string.Equals(r.FilePath, f, StringComparison.OrdinalIgnoreCase) && OcrVersion.IsCurrent(r, options.OcrModel));
            SubtitlePlan MakePlan()
            {
                SubtitlePlan made;
                lock (state)
                {
                    var existing = options.PreferExistingFiles ? ExistingSubtitles.Find(path, state.MadeFileSet()) : null;
                    made = SubtitlePlanner.Plan(before, settings, path, existing, OcrFileIsCurrent);
                }

                // Files Emby saved from the subtitle search, made by an older OCR: redone in place, like our own OCR files.
                if (redoOlder)
                    made.Ocr.AddRange(SearchOcrRedo.Jobs(path, before, searchRecords, options.OcrModel)
                        .Where(j => !made.Ocr.Any(o => string.Equals(o.OutputPath, j.OutputPath, StringComparison.OrdinalIgnoreCase))));
                return made;
            }
            var plan = MakePlan();

            // The video is about to be rewritten: the last chance for a perfect (hash) match. A download is planned with.
            if (plan.NeedsRemux && options.DownloadHashMatchedSubtitles && _hashSubtitles != null && IsOriginal(path, state)
                && !_hashSubtitles.AlreadyChecked(path))
            {
                var languages = HashMatchedSubtitles.LanguagesToSearch(settings.KeepLanguages,
                    before.streams.Where(s => s.IsSubtitle && SubtitlePlanner.IsTextCodec(s.codec_name)).Select(s => s.Language),
                    ExistingSubtitles.Find(path, new string[0]).Keys);
                var lookup = await _hashSubtitles.Lookup(item, languages, options.DryRun ? HashMode.LogOnly : HashMode.Download, ct, options.HashDownloadsPerDay).ConfigureAwait(false);
                if (lookup.Check == HashCheck.TryLater) return Checked.Finished(Outcome.NothingToDo);
                if (lookup.Downloaded.Count > 0) plan = MakePlan();
            }

            if (!plan.HasWork)
            {
                _log.Debug("SubRemover: nothing to do for {0}", path);
                if (!options.DryRun)
                {
                    lock (state)
                    {
                        state.Processed[path] = stamp;
                        store.Save(state);
                    }
                }
                return Checked.Finished(Outcome.NothingToDo);
            }

            LogPlan(path, plan, options.DryRun);
            if (options.DryRun)
            {
                lock (Totals) Totals.AddPlan(plan);
                return Checked.Finished(Outcome.WouldChange);
            }
            return new Checked { Item = item, Path = path, Before = before, Plan = plan };
        }

        /// <summary>
        /// Changes a checked video: extract, OCR, rewrite. One video at a time (the run calls it in turn), while the next
        /// videos are being checked.
        /// </summary>
        public async Task<Outcome> Change(Checked video, PluginOptions options, SubremoverState state, StateStore store, CancellationToken ct)
        {
            if (video.Outcome != null) return video.Outcome.Value;
            var path = video.Path;

            // AudioRemover rewrites videos the same way; two rewrites of one file at once would lose one of them.
            if (OtherToolIsWorkingOn(path))
            {
                _log.Info("SubRemover: AudioRemover is changing {0} right now; leaving it for the next run", path);
                return Outcome.NothingToDo;
            }

            lock (state)
            {
                state.InProgress = path;
                store.Save(state);
            }
            try
            {
                return await Apply(video.Item, path, video.Before, video.Plan, options, state, store, ct).ConfigureAwait(false);
            }
            finally
            {
                // Catches temp files from any step that was cancelled or failed part-way.
                foreach (var file in Leftovers.Clean(path)) _log.Debug("SubRemover: deleted leftover {0}", file);
                lock (state)
                {
                    state.InProgress = null;
                    store.Save(state);
                }
            }
        }

        /// <summary>What checking a video found: either the outcome already, or the plan to apply.</summary>
        public sealed class Checked
        {
            public Outcome? Outcome { get; set; }
            public BaseItem Item { get; set; }
            public string Path { get; set; }
            public ProbeResult Before { get; set; }
            public SubtitlePlan Plan { get; set; }

            public static Checked Finished(Outcome outcome) => new Checked { Outcome = outcome };
        }

        /// <summary>
        /// AudioRemover's temporary file next to the video, still being written: it's rewriting that video at this moment.
        /// One left behind by a crash stops being written to, so it doesn't block the video for good.
        /// </summary>
        internal static bool OtherToolIsWorkingOn(string path, DateTime? nowUtc = null)
        {
            var temp = new FileInfo(path + ".audioremover-tmp");
            return temp.Exists && (nowUtc ?? DateTime.UtcNow) - temp.LastWriteTimeUtc < TimeSpan.FromMinutes(10);
        }

        /// <summary>
        /// Not yet rewritten by SubRemover (no backup of it) or AudioRemover (no backup beside it): its hash is still the
        /// one subtitle sites know. Videos changed before hashes were kept are passed over.
        /// </summary>
        private static bool IsOriginal(string path, SubremoverState state)
        {
            lock (state)
            {
                if (state.Backups.Any(b => string.Equals(b.OriginalPath, path, StringComparison.OrdinalIgnoreCase))) return false;
            }
            return !File.Exists(path + ".audioremover.bak");
        }

        private async Task<Outcome> Apply(BaseItem item, string path, ProbeResult before, SubtitlePlan plan, PluginOptions options,
            SubremoverState state, StateStore store, CancellationToken ct)
        {
            // What was made next to the video, for the backup's note in the restore list.
            var made = new List<MadeFile>();
            foreach (var job in plan.Extract)
            {
                var extracted = await Extract(path, job, before.FrameCountsAreCurrent, state.MadeFileSet(), ct).ConfigureAwait(false);
                if (extracted == ExtractOutcome.Failed) return Outcome.Failed;
                if (extracted == ExtractOutcome.KeepTrackInVideo && plan.Remove.Remove(job.Stream)) plan.Keep.Add(job.Stream);
                if (extracted == ExtractOutcome.Done)
                {
                    made.Add(new MadeFile { Path = job.OutputPath });
                    lock (state) state.MadeFileSet().Add(job.OutputPath);
                }
            }

            // OCR before the remux: the stream indexes in the plan belong to the original file.
            var ocrFailed = false;
            foreach (var job in plan.Ocr)
            {
                job.Models = options.OcrModel;
                var problem = await SubtitleOcr.Run(_runner, _tessdata, path, job, ct).ConfigureAwait(false);
                if (problem == null)
                {
                    _log.Info("SubRemover: OCR {0} -> {1}", job.Stream.Describe(), job.OutputPath);
                    lock (Totals) Totals.AddOcr(job.OutputPath);
                    made.Add(new MadeFile { Path = job.OutputPath, Ocr = true });
                    if (job.SearchRecord != null)
                    {
                        // Still Emby's file (it shows up as a downloaded subtitle); only its record changes.
                        var redone = job.SearchRecord;
                        var text = File.ReadAllText(job.OutputPath);
                        _searchOcr.Update(list =>
                        {
                            foreach (var r in list.Where(r => string.Equals(r.VideoPath, redone.VideoPath, StringComparison.OrdinalIgnoreCase) && r.StreamIndex == redone.StreamIndex))
                            {
                                var fresh = OcrVersion.Record(job.Models);
                                r.Version = fresh.Version; r.Model = fresh.Model; r.MadeUtc = fresh.MadeUtc;
                                r.TextHash = OcrVersion.TextHash(text);
                                r.FilePath = job.OutputPath;
                            }
                        });
                    }
                    else
                    {
                        lock (state)
                        {
                            state.MadeFileSet().Add(job.OutputPath);
                            state.OcrFiles[job.OutputPath] = OcrVersion.Record(job.Models);
                        }
                    }
                }
                else
                {
                    _log.Warn("SubRemover: OCR of {0} in {1} failed: {2}. Will retry next run.", job.Stream.Describe(), path, problem);
                    ocrFailed = true;
                    lock (Totals) Totals.OcrFailed++;
                }
            }

            if (plan.NeedsRemux)
            {
                var notes = BackupNotes.Describe(path, plan, made, options.OcrModel);
                if (!await RemuxAndSwap(path, before, plan, options, state, notes, ct).ConfigureAwait(false)) return Outcome.Failed;
                lock (Totals) Totals.AddRemux(plan);
            }

            if (!ocrFailed)
            {
                lock (state)
                {
                    state.Processed[path] = FileStamp.Of(path);
                    store.Save(state);
                }
            }

            _providerManager.QueueRefresh(item.InternalId, new MetadataRefreshOptions(_fileSystem)
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = false
            }, RefreshPriority.Normal);

            return ocrFailed ? Outcome.Failed : Outcome.Changed;
        }

        private async Task GuessLanguages(string path, ProbeResult probe, PlannerSettings settings, GuessRules rules, CancellationToken ct)
        {
            var readings = await SubtitleLanguageGuessing.Run(_runner, path, probe, ct, rules, s => SubtitlePlanner.MightKeep(s, settings))
                .ConfigureAwait(false);
            foreach (var r in readings)
            {
                var guess = r.Guess;
                var sdh = guess.SdhReason != null ? $"; looks like SDH ({guess.SdhReason})" : "";
                var onlyFiles = rules.UntaggedFilesOnly ? ". Only used to name its extracted file; the track stays as it is" : "";
                var wrongTagOutcome = !rules.FixWrongTags
                    ? ". Left as tagged (Subtitles tagged with the wrong language: Leave them); only used to name its extracted file"
                    : guess.Language != null ? ". The tag is fixed" : r.Stream.RemoveAsWrongTag ? ". It gets no language and is removed" : ", so it gets no language";
                if (guess.SdhReason != null) lock (Totals) Totals.SdhSpotted++;
                // One pre-built strings: paths may contain braces.
                if (r.Tag == "und")
                {
                    if (guess.Language != null) lock (Totals) Totals.LanguagesGuessed++;
                    _log.Info(guess.Language != null
                        ? $"SubRemover: {path} track #{r.Stream.index} has no language set; guessed {Languages.NameOf(guess.Language)} ({guess.Reason}){sdh}{onlyFiles}"
                        : $"SubRemover: {path} track #{r.Stream.index} has no language set and stays that way: {guess.Reason}{sdh}");
                }
                else if (r.WrongTag)
                {
                    lock (Totals) Totals.WrongTags++;
                    if (rules.FixWrongTags) lock (Totals) Totals.WrongTagsFixed++;
                    _log.Info(guess.Language != null
                        ? $"SubRemover: {path} track #{r.Stream.index} is tagged {Languages.NameOf(r.Tag)} but reads as {Languages.NameOf(guess.Language)} ({guess.Reason}){sdh}{wrongTagOutcome}"
                        : $"SubRemover: {path} track #{r.Stream.index} is tagged {Languages.NameOf(r.Tag)} but its text isn't {Languages.NameOf(r.Tag)} " +
                          $"({guess.Hits[r.Tag]} of {guess.Words} words are common {Languages.NameOf(r.Tag)} words); its language is unknown{sdh}" +
                          (rules.FixWrongTags ? wrongTagOutcome : ". Left as tagged (Subtitles tagged with the wrong language: Leave them); it gets no extracted file"));
                }
                else if (guess.SdhReason != null)
                {
                    _log.Info($"SubRemover: {path} track #{r.Stream.index} ({Languages.NameOf(r.Tag)}) shares its language with another track{sdh}");
                }
                else
                {
                    _log.Debug($"SubRemover: {path} track #{r.Stream.index} shares its language with another track; its text agrees with its tag ({guess.Language ?? "can't tell: " + guess.Reason})");
                }
            }
        }

        // Logged as one pre-built string: paths and titles may contain braces that would break format placeholders.
        private void LogPlan(string path, SubtitlePlan plan, bool dryRun) => _log.Info(DescribePlan(path, plan, dryRun));

        internal static string DescribePlan(string path, SubtitlePlan plan, bool dryRun)
        {
            string Streams(System.Collections.Generic.IEnumerable<ProbeStream> s) => s.Any() ? string.Join(", ", s.Select(x => x.Describe())) : "none";
            string Files(System.Collections.Generic.IEnumerable<(ProbeStream Stream, string Output)> jobs) =>
                jobs.Any() ? string.Join(", ", jobs.Select(j => j.Stream.Describe() + " -> " + Path.GetFileName(j.Output))) : "none";

            return "SubRemover " + (dryRun ? "[TEST MODE - no changes] " : "") + path +
                   "\n    keep:    " + Streams(plan.Keep) +
                   "\n    remove:  " + Streams(plan.Remove) +
                   "\n    extract: " + Files(plan.Extract.Select(e => (e.Stream, e.OutputPath))) +
                   "\n    ocr:     " + Files(plan.Ocr.Select(e => (e.Stream, e.OutputPath))) +
                   (plan.NewDefault != null ? "\n    default: " + plan.NewDefault.Describe() : "") +
                   (plan.LeftToExistingFiles.Count > 0
                       ? "\n    existing: " + string.Join(", ", plan.LeftToExistingFiles.Select(kv => kv.Key + " -> " + kv.Value)) +
                         " (no file of ours in that language" + (plan.ClearDefault.Count > 0 ? "; default flag off " + Streams(plan.ClearDefault) : "") +
                         (plan.DefaultStaysOn.Count > 0 ? "; default flag stays on " + Streams(plan.DefaultStaysOn) + ": MP4 always keeps one subtitle on" : "") + ")"
                       : "") +
                   (plan.LanguageChanges().Count > 0 ? "\n    language: " + Streams(plan.Keep.Where(s => s.LanguageGuessed)) + " written into the video" : "");
        }

        private async Task<ProbeResult> Probe(string path, CancellationToken ct)
        {
            var result = await _runner.RunFfprobe(FfmpegArgs.Probe(path), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _log.Error("SubRemover: ffprobe failed for {0} (exit {1}): {2}", path, result.ExitCode, result.StdErr);
                return null;
            }
            try
            {
                return _json.DeserializeFromString<ProbeResult>(result.StdOut);
            }
            catch (Exception ex)
            {
                _log.ErrorException("SubRemover: could not read ffprobe output for {0}", ex, path);
                return null;
            }
        }

        private enum ExtractOutcome
        {
            Done,
            Failed,
            KeepTrackInVideo
        }

        private async Task<ExtractOutcome> Extract(string videoPath, ExtractJob job, bool compareCount, ISet<string> madeFiles, CancellationToken ct)
        {
            var isAss = !job.ConvertToSrt;
            if (File.Exists(job.OutputPath))
            {
                // Someone else's file, or one cut short earlier: it never replaces the embedded track unless it's complete.
                var existingProblem = OutputChecks.CheckSubtitleFile(job.OutputPath, job.Stream, isAss, compareCount);
                if (existingProblem == null)
                {
                    _log.Info("SubRemover: {0} already exists and looks complete, not overwriting", job.OutputPath);
                    return ExtractOutcome.Done;
                }
                _log.Warn("SubRemover: existing {0} looks incomplete ({1}). Leaving that file alone and keeping {2} in the video.",
                    job.OutputPath, existingProblem, job.Stream.Describe());
                return ExtractOutcome.KeepTrackInVideo;
            }

            var temp = job.OutputPath + TempSuffix;
            var result = await _runner.RunFfmpeg(FfmpegArgs.Extract(videoPath, job.Stream.index, temp, job.ConvertToSrt), ct).ConfigureAwait(false);
            var problem = !result.Success
                ? $"ffmpeg exit {result.ExitCode}: {result.StdErr}"
                : OutputChecks.TruncationMessage(result.StdErr) ?? OutputChecks.CheckSubtitleFile(temp, job.Stream, isAss, compareCount);

            if (problem == null)
            {
                File.Move(temp, job.OutputPath);
                _log.Info("SubRemover: extracted {0} -> {1}", job.Stream.Describe(), job.OutputPath);
                lock (Totals) Totals.AddExtracted(job.OutputPath);
                return ExtractOutcome.Done;
            }

            _log.Error("SubRemover: extracting {0} from {1} failed ({2}). Video left unchanged.", job.Stream.Describe(), videoPath, problem);
            TryDelete(temp);
            return ExtractOutcome.Failed;
        }

        private async Task<bool> RemuxAndSwap(string path, ProbeResult before, SubtitlePlan plan, PluginOptions options, SubremoverState state,
            List<string> notes, CancellationToken ct)
        {
            var muxer = FfmpegArgs.MuxerFor(path);
            if (muxer == null)
            {
                _log.Warn("SubRemover: unsupported container for {0}, skipping", path);
                return false;
            }

            var temp = path + TempSuffix;
            TryDelete(temp);
            var beforeBytes = new FileInfo(path).Length;
            _libraryMonitor.ReportFileSystemChangeBeginning(path);
            try
            {
                var result = await _runner.RunFfmpeg(FfmpegArgs.Remux(path, temp, muxer, plan.Remove.Select(s => s.index), plan.DefaultChanges(), plan.LanguageChanges()), ct).ConfigureAwait(false);
                var failure = !result.Success ? $"exit {result.ExitCode}: {result.StdErr}" : OutputChecks.TruncationMessage(result.StdErr);
                if (failure != null)
                {
                    _log.Error("SubRemover: ffmpeg failed for {0} ({1}). Original left unchanged.", path, failure);
                    TryDelete(temp);
                    return false;
                }

                var after = await Probe(temp, ct).ConfigureAwait(false);
                var problem = RemuxVerifier.Verify(before, after, plan.Remove.Count, beforeBytes, new FileInfo(temp).Length)
                              ?? (plan.NewDefault != null
                                  ? RemuxVerifier.CheckDefault(after, FfmpegArgs.OutputIndex(plan.NewDefault.index, plan.Remove.Select(s => s.index)))
                                  : null)
                              ?? RemuxVerifier.CheckLanguages(after, plan.LanguageChanges().ToDictionary(
                                  l => FfmpegArgs.OutputIndex(l.Key, plan.Remove.Select(s => s.index)), l => l.Value))
                              ?? RemuxVerifier.CheckNotDefault(after, plan.ClearDefault.Select(s => FfmpegArgs.OutputIndex(s.index, plan.Remove.Select(r => r.index))));
                if (problem != null)
                {
                    _log.Error("SubRemover: new file for {0} failed verification ({1}). Original left unchanged.", path, problem);
                    TryDelete(temp);
                    return false;
                }

                return Swap(path, temp, options, state, notes);
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                throw;
            }
            finally
            {
                _libraryMonitor.ReportFileSystemChangeComplete(path, false);
            }
        }

        private bool Swap(string path, string temp, PluginOptions options, SubremoverState state, List<string> notes)
        {
            var result = BackupManager.Replace(path, temp, options.BackupMode, options.BackupFolder);
            if (!result.Success)
            {
                _log.Error("SubRemover: {0}: {1}", path, result.Message);
                return false;
            }

            if (result.Message != null) _log.Debug("SubRemover: {0}: {1}", path, result.Message);
            lock (state)
            {
                if (result.NewBackup != null)
                {
                    result.NewBackup.Changes = notes;
                    state.Backups.Add(result.NewBackup);
                }
                else if (result.BackupPath != null)
                {
                    // The backup from an earlier run is still the original: it now undoes this run's changes too.
                    var earlier = state.Backups.LastOrDefault(b => string.Equals(b.OriginalPath, path, StringComparison.OrdinalIgnoreCase));
                    if (earlier?.Changes != null) earlier.Changes = earlier.Changes.Concat(notes ?? new List<string>()).Distinct().ToList();
                }
            }
            _log.Info("SubRemover: updated {0}{1}", path, result.BackupPath != null ? " (backup: " + result.BackupPath + ")" : " (no backup kept)");
            return true;
        }

        private static void TryDelete(string path) => BackupManager.TryDelete(path);
    }
}
