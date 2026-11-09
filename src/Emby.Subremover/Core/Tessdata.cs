using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Subremover.Core
{
    /// <summary>Which Tesseract model reads picture subtitles.</summary>
    public enum OcrModels
    {
        /// <summary>tessdata_fast: about 4 MB per language.</summary>
        Fast,

        /// <summary>tessdata_best: slower and bigger (about 15 MB per language), better on some words, worse on others.</summary>
        Accurate,

        /// <summary>Both at once, keeping the better reading of each subtitle.</summary>
        Both
    }

    /// <summary>
    /// Tesseract language files, downloaded on first use into "&lt;root&gt;/tessdata" (fast model) and
    /// "&lt;root&gt;/tessdata_best" (accurate model).
    /// </summary>
    public class Tessdata
    {
        private const string FastUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/{0}.traineddata";
        private const string BestUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/{0}.traineddata";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly SemaphoreSlim DownloadLock = new SemaphoreSlim(1, 1);

        public Tessdata(string root)
        {
            Root = root;
        }

        /// <summary>The folder that contains "tessdata"; ffmpeg must run with this as its working directory.</summary>
        public string Root { get; }

        /// <summary>The model's folder inside <see cref="Root"/>.</summary>
        public static string Folder(bool best) => best ? "tessdata_best" : "tessdata";

        public string FileFor(string language, bool best = false) => Path.Combine(Root, Folder(best), language + ".traineddata");

        /// <returns>null when the file is ready, otherwise why it couldn't be downloaded.</returns>
        public async Task<string> Ensure(string language, CancellationToken ct, bool best = false)
        {
            var file = FileFor(language, best);
            if (File.Exists(file)) return null;

            await DownloadLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(file)) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var temp = file + ".download";

                using (var response = await Http.GetAsync(string.Format(best ? BestUrl : FastUrl, language), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return $"download of {Folder(best)}/{language}.traineddata failed: HTTP {(int)response.StatusCode}";
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var target = File.Create(temp))
                    {
                        await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
                    }
                }

                File.Move(temp, file);
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return $"download of {Folder(best)}/{language}.traineddata failed: {ex.Message}";
            }
            finally
            {
                DownloadLock.Release();
            }
        }
    }

    public static class SubtitleOcr
    {
        /// <returns>null on success, otherwise the reason it failed.</returns>
        public static async Task<string> Run(FfmpegRunner runner, Tessdata tessdata, string videoPath, OcrJob job, CancellationToken ct)
        {
            // false = fast model, true = accurate model.
            var models = job.Models == OcrModels.Both ? new[] { false, true } : new[] { job.Models == OcrModels.Accurate };
            foreach (var best in models)
            {
                var problem = await tessdata.Ensure(job.TesseractLanguage, ct, best).ConfigureAwait(false);
                if (problem != null) return problem;
            }

            var temps = models.Select(best => job.OutputPath + (best ? ".best" : "") + Leftovers.TempSuffix).ToList();
            try
            {
                // With both models, two ffmpeg processes run at the same time, each on its own CPU core
                // (Emby's ffmpeg runs two filters in one process one after the other).
                var results = await Task.WhenAll(models.Select((best, i) => runner.RunFfmpeg(
                    FfmpegArgs.Ocr(videoPath, job.Stream.index, job.TesseractLanguage, temps[i], Tessdata.Folder(best)), ct, tessdata.Root))).ConfigureAwait(false);

                for (var i = 0; i < results.Length; i++)
                {
                    var failure = !results[i].Success
                        ? $"ffmpeg OCR failed (exit {results[i].ExitCode}): {results[i].StdErr}"
                        : OutputChecks.TruncationMessage(results[i].StdErr) ?? OutputChecks.CheckSubtitleFile(temps[i], job.Stream, isAss: false, compareCount: false);
                    if (failure != null) return failure;
                }

                // ffmpeg leaves picture subtitles on screen for 1193 hours and misreads letters; see OcrCleanup.
                var texts = temps.Select(File.ReadAllText).ToList();
                var cleaned = texts.Count == 1
                    ? OcrCleanup.Clean(texts[0], job.TesseractLanguage)
                    : OcrCleanup.CleanBoth(texts[0], texts[1], job.TesseractLanguage);
                File.WriteAllText(temps[0], cleaned);

                if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath);
                File.Move(temps[0], job.OutputPath);
                return null;
            }
            finally
            {
                foreach (var temp in temps) BackupManager.TryDelete(temp);
            }
        }
    }
}
