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
        private const string TempSuffix = ".subremover-tmp";

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
            ExtractTextSubtitles = o.ExtractTextSubtitles,
            RemoveExtractedFromVideo = o.ExtractTextSubtitles && o.RemoveExtractedFromVideo,
            OcrPictureSubtitles = o.OcrPictureSubtitles,
            OcrReplaceExisting = o.OcrExistingFile == ExistingOcrFile.Replace
        };

        public async Task<Outcome> Process(BaseItem item, PluginOptions options, SubremoverState state, StateStore store, CancellationToken ct)
        {
            var path = item.Path;
            var stamp = FileStamp.Of(path);
            if (state.Processed.TryGetValue(path, out var done) && done.Matches(stamp)) return Outcome.AlreadyDone;

            var before = await Probe(path, ct).ConfigureAwait(false);
            if (before == null) return Outcome.Failed;

            var plan = SubtitlePlanner.Plan(before, SettingsFrom(options), path);
            if (!plan.HasWork)
            {
                _log.Debug("Subremover: nothing to do for {0}", path);
                if (!options.DryRun)
                {
                    state.Processed[path] = stamp;
                    store.Save(state);
                }
                return Outcome.NothingToDo;
            }

            LogPlan(path, plan, options.DryRun);
            if (options.DryRun) return Outcome.WouldChange;

            foreach (var job in plan.Extract)
            {
                if (!await Extract(path, job, ct).ConfigureAwait(false)) return Outcome.Failed;
            }

            // OCR before the remux: the stream indexes in the plan belong to the original file.
            var ocrFailed = false;
            foreach (var job in plan.Ocr)
            {
                var problem = await SubtitleOcr.Run(_runner, _tessdata, path, job, ct).ConfigureAwait(false);
                if (problem == null)
                {
                    _log.Info("Subremover: OCR {0} -> {1}", job.Stream.Describe(), job.OutputPath);
                }
                else
                {
                    _log.Warn("Subremover: OCR of {0} in {1} failed: {2}. Will retry next run.", job.Stream.Describe(), path, problem);
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

        private void LogPlan(string path, SubtitlePlan plan, bool dryRun)
        {
            string List(System.Collections.Generic.IEnumerable<ProbeStream> s) => s.Any() ? string.Join(", ", s.Select(x => x.Describe())) : "none";

            _log.Info("Subremover {0}{1}\n    keep:    {2}\n    remove:  {3}\n    extract: {4}",
                dryRun ? "[TEST MODE - no changes] " : "",
                path,
                List(plan.Keep),
                List(plan.Remove),
                plan.Extract.Any() ? string.Join(", ", plan.Extract.Select(e => e.Stream.Describe() + " -> " + Path.GetFileName(e.OutputPath))) : "none",
                plan.Ocr.Any() ? string.Join(", ", plan.Ocr.Select(e => e.Stream.Describe() + " -> " + Path.GetFileName(e.OutputPath))) : "none");
        }

        private async Task<ProbeResult> Probe(string path, CancellationToken ct)
        {
            var result = await _runner.RunFfprobe(FfmpegArgs.Probe(path), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _log.Error("Subremover: ffprobe failed for {0} (exit {1}): {2}", path, result.ExitCode, result.StdErr);
                return null;
            }
            try
            {
                return _json.DeserializeFromString<ProbeResult>(result.StdOut);
            }
            catch (Exception ex)
            {
                _log.ErrorException("Subremover: could not read ffprobe output for {0}", ex, path);
                return null;
            }
        }

        private async Task<bool> Extract(string videoPath, ExtractJob job, CancellationToken ct)
        {
            if (File.Exists(job.OutputPath))
            {
                _log.Info("Subremover: {0} already exists, not overwriting", job.OutputPath);
                return true;
            }

            var result = await _runner.RunFfmpeg(FfmpegArgs.Extract(videoPath, job.Stream.index, job.OutputPath, job.ConvertToSrt), ct).ConfigureAwait(false);
            if (result.Success && File.Exists(job.OutputPath) && new FileInfo(job.OutputPath).Length > 0)
            {
                _log.Info("Subremover: extracted {0} -> {1}", job.Stream.Describe(), job.OutputPath);
                return true;
            }

            _log.Error("Subremover: extracting {0} from {1} failed (exit {2}): {3}. Video left unchanged.",
                job.Stream.Describe(), videoPath, result.ExitCode, result.StdErr);
            TryDelete(job.OutputPath);
            return false;
        }

        private async Task<bool> RemuxAndSwap(string path, ProbeResult before, SubtitlePlan plan, PluginOptions options, SubremoverState state, CancellationToken ct)
        {
            var muxer = FfmpegArgs.MuxerFor(path);
            if (muxer == null)
            {
                _log.Warn("Subremover: unsupported container for {0}, skipping", path);
                return false;
            }

            var temp = path + TempSuffix;
            var beforeBytes = new FileInfo(path).Length;
            _libraryMonitor.ReportFileSystemChangeBeginning(path);
            try
            {
                var result = await _runner.RunFfmpeg(FfmpegArgs.Remux(path, temp, muxer, plan.Remove.Select(s => s.index)), ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    _log.Error("Subremover: ffmpeg failed for {0} (exit {1}): {2}. Original left unchanged.", path, result.ExitCode, result.StdErr);
                    TryDelete(temp);
                    return false;
                }

                var after = await Probe(temp, ct).ConfigureAwait(false);
                var problem = RemuxVerifier.Verify(before, after, plan.Remove.Count, beforeBytes, new FileInfo(temp).Length);
                if (problem != null)
                {
                    _log.Error("Subremover: new file for {0} failed verification ({1}). Original left unchanged.", path, problem);
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
                _log.Error("Subremover: {0}: {1}", path, result.Message);
                return false;
            }

            if (result.Message != null) _log.Debug("Subremover: {0}: {1}", path, result.Message);
            if (result.NewBackup != null) state.Backups.Add(result.NewBackup);
            _log.Info("Subremover: updated {0}{1}", path, result.BackupPath != null ? " (backup: " + result.BackupPath + ")" : " (no backup kept)");
            return true;
        }

        private static void TryDelete(string path) => BackupManager.TryDelete(path);
    }
}
