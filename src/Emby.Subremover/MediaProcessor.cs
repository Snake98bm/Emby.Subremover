using System;
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

        public MediaProcessor(FfmpegRunner runner, ILibraryMonitor libraryMonitor, IProviderManager providerManager,
            IFileSystem fileSystem, IJsonSerializer json, ILogger log, Tessdata tessdata)
        {
            _tessdata = tessdata;
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
            OcrReplaceExisting = o.OcrExistingFile == ExistingOcrFile.Replace
        };

        public async Task<Outcome> Process(BaseItem item, PluginOptions options, SubremoverState state, StateStore store, CancellationToken ct)
        {
            var path = item.Path;
            if (state.IsRestored(path))
            {
                _log.Debug("SubRemover: {0} was restored from its backup, leaving it alone", path);
                return Outcome.AlreadyDone;
            }

            var stamp = FileStamp.Of(path);
            if (state.Processed.TryGetValue(path, out var done) && done.Matches(stamp)) return Outcome.AlreadyDone;

            var before = await Probe(path, ct).ConfigureAwait(false);
            if (before == null) return Outcome.Failed;

            var plan = SubtitlePlanner.Plan(before, SettingsFrom(options), path);
            if (!plan.HasWork)
            {
                _log.Debug("SubRemover: nothing to do for {0}", path);
                if (!options.DryRun)
                {
                    state.Processed[path] = stamp;
                    store.Save(state);
                }
                return Outcome.NothingToDo;
            }

            LogPlan(path, plan, options.DryRun);
            if (options.DryRun) return Outcome.WouldChange;

            state.InProgress = path;
            store.Save(state);
            try
            {
                return await Apply(item, path, before, plan, options, state, store, ct).ConfigureAwait(false);
            }
            finally
            {
                // Catches temp files from any step that was cancelled or failed part-way.
                foreach (var file in Leftovers.Clean(path)) _log.Debug("SubRemover: deleted leftover {0}", file);
                state.InProgress = null;
                store.Save(state);
            }
        }

        private async Task<Outcome> Apply(BaseItem item, string path, ProbeResult before, SubtitlePlan plan, PluginOptions options,
            SubremoverState state, StateStore store, CancellationToken ct)
        {
            foreach (var job in plan.Extract)
            {
                var extracted = await Extract(path, job, before.FrameCountsAreCurrent, ct).ConfigureAwait(false);
                if (extracted == ExtractOutcome.Failed) return Outcome.Failed;
                if (extracted == ExtractOutcome.KeepTrackInVideo && plan.Remove.Remove(job.Stream)) plan.Keep.Add(job.Stream);
            }

            // OCR before the remux: the stream indexes in the plan belong to the original file.
            var ocrFailed = false;
            foreach (var job in plan.Ocr)
            {
                var problem = await SubtitleOcr.Run(_runner, _tessdata, path, job, ct).ConfigureAwait(false);
                if (problem == null)
                {
                    _log.Info("SubRemover: OCR {0} -> {1}", job.Stream.Describe(), job.OutputPath);
                }
                else
                {
                    _log.Warn("SubRemover: OCR of {0} in {1} failed: {2}. Will retry next run.", job.Stream.Describe(), path, problem);
                    ocrFailed = true;
                }
            }

            if (plan.NeedsRemux)
            {
                if (!await RemuxAndSwap(path, before, plan, options, state, ct).ConfigureAwait(false)) return Outcome.Failed;
            }

            if (!ocrFailed)
            {
                state.Processed[path] = FileStamp.Of(path);
                store.Save(state);
            }

            _providerManager.QueueRefresh(item.InternalId, new MetadataRefreshOptions(_fileSystem)
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = false
            }, RefreshPriority.Normal);

            return ocrFailed ? Outcome.Failed : Outcome.Changed;
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
                   "\n    ocr:     " + Files(plan.Ocr.Select(e => (e.Stream, e.OutputPath)));
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

        private async Task<ExtractOutcome> Extract(string videoPath, ExtractJob job, bool compareCount, CancellationToken ct)
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
                return ExtractOutcome.Done;
            }

            _log.Error("SubRemover: extracting {0} from {1} failed ({2}). Video left unchanged.", job.Stream.Describe(), videoPath, problem);
            TryDelete(temp);
            return ExtractOutcome.Failed;
        }

        private async Task<bool> RemuxAndSwap(string path, ProbeResult before, SubtitlePlan plan, PluginOptions options, SubremoverState state, CancellationToken ct)
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
                var result = await _runner.RunFfmpeg(FfmpegArgs.Remux(path, temp, muxer, plan.Remove.Select(s => s.index)), ct).ConfigureAwait(false);
                var failure = !result.Success ? $"exit {result.ExitCode}: {result.StdErr}" : OutputChecks.TruncationMessage(result.StdErr);
                if (failure != null)
                {
                    _log.Error("SubRemover: ffmpeg failed for {0} ({1}). Original left unchanged.", path, failure);
                    TryDelete(temp);
                    return false;
                }

                var after = await Probe(temp, ct).ConfigureAwait(false);
                var problem = RemuxVerifier.Verify(before, after, plan.Remove.Count, beforeBytes, new FileInfo(temp).Length);
                if (problem != null)
                {
                    _log.Error("SubRemover: new file for {0} failed verification ({1}). Original left unchanged.", path, problem);
                    TryDelete(temp);
                    return false;
                }

                return Swap(path, temp, options, state);
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

        private bool Swap(string path, string temp, PluginOptions options, SubremoverState state)
        {
            var result = BackupManager.Replace(path, temp, options.BackupMode, options.BackupFolder);
            if (!result.Success)
            {
                _log.Error("SubRemover: {0}: {1}", path, result.Message);
                return false;
            }

            if (result.Message != null) _log.Debug("SubRemover: {0}: {1}", path, result.Message);
            if (result.NewBackup != null) state.Backups.Add(result.NewBackup);
            _log.Info("SubRemover: updated {0}{1}", path, result.BackupPath != null ? " (backup: " + result.BackupPath + ")" : " (no backup kept)");
            return true;
        }

        private static void TryDelete(string path) => BackupManager.TryDelete(path);
    }
}
