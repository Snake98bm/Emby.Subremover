using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Subremover.Core
{
    /// <summary>Tesseract language files, downloaded on first use into "&lt;root&gt;/tessdata".</summary>
    public class Tessdata
    {
        private const string DownloadUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/{0}.traineddata";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly SemaphoreSlim DownloadLock = new SemaphoreSlim(1, 1);

        public Tessdata(string root)
        {
            Root = root;
        }

        /// <summary>The folder that contains "tessdata"; ffmpeg must run with this as its working directory.</summary>
        public string Root { get; }

        public string FileFor(string language) => Path.Combine(Root, "tessdata", language + ".traineddata");

        /// <returns>null when the file is ready, otherwise why it couldn't be downloaded.</returns>
        public async Task<string> Ensure(string language, CancellationToken ct)
        {
            var file = FileFor(language);
            if (File.Exists(file)) return null;

            await DownloadLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (File.Exists(file)) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var temp = file + ".download";

                using (var response = await Http.GetAsync(string.Format(DownloadUrl, language), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return $"download of {language}.traineddata failed: HTTP {(int)response.StatusCode}";
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
                return $"download of {language}.traineddata failed: {ex.Message}";
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
            var problem = await tessdata.Ensure(job.TesseractLanguage, ct).ConfigureAwait(false);
            if (problem != null) return problem;

            var temp = job.OutputPath + ".subremover-tmp";
            var result = await runner.RunFfmpeg(FfmpegArgs.Ocr(videoPath, job.Stream.index, job.TesseractLanguage, temp), ct, tessdata.Root).ConfigureAwait(false);

            var failure = !result.Success
                ? $"ffmpeg OCR failed (exit {result.ExitCode}): {result.StdErr}"
                : OutputChecks.TruncationMessage(result.StdErr) ?? OutputChecks.CheckSubtitleFile(temp, job.Stream, isAss: false, compareCount: false);
            if (failure != null)
            {
                BackupManager.TryDelete(temp);
                return failure;
            }

            if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath);
            File.Move(temp, job.OutputPath);
            return null;
        }
    }
}
