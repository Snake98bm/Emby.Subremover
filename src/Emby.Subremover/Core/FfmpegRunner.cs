using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;

namespace Emby.Subremover.Core
{
    public class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public bool Success => ExitCode == 0;
    }

    public class FfmpegRunner
    {
        private readonly Func<string> _ffmpegPath;
        private readonly Func<string> _ffprobePath;
        private readonly Func<IEnumerable<KeyValuePair<string, string>>> _environment;

        // Funcs, not values: the server admin can change the ffmpeg location while Emby is running.
        public FfmpegRunner(Func<string> ffmpegPath, Func<string> ffprobePath, Func<IEnumerable<KeyValuePair<string, string>>> environment = null)
        {
            _ffmpegPath = ffmpegPath;
            _ffprobePath = ffprobePath;
            _environment = environment ?? (() => Enumerable.Empty<KeyValuePair<string, string>>());
        }

        public static FfmpegRunner FromEmby(IFfmpegManager ffmpeg) => new FfmpegRunner(
            () => ffmpeg.FfmpegConfiguration.EncoderPath,
            () => ffmpeg.FfmpegConfiguration.ProbePath,
            () => (ffmpeg.FfmpegConfiguration.EnvironmentVariables ?? Array.Empty<EnvironmentVariableSetting>())
                .Select(v => new KeyValuePair<string, string>(v.VariableName, v.Value)));

        public Task<ProcessResult> RunFfmpeg(IList<string> args, CancellationToken ct, string workingDirectory = null) =>
            Run(_ffmpegPath(), args, ct, workingDirectory);

        public Task<ProcessResult> RunFfprobe(IList<string> args, CancellationToken ct) => Run(_ffprobePath(), args, ct, null);

        private async Task<ProcessResult> Run(string exe, IList<string> args, CancellationToken ct, string workingDirectory)
        {
            var psi = new ProcessStartInfo(exe, FfmpegArgs.ToCommandLine(args))
            {
                WorkingDirectory = workingDirectory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                // ffmpeg writes UTF-8; without this Windows decodes it with the ANSI code page ("Español" -> "EspaÃ±ol").
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var variable in _environment())
            {
                psi.EnvironmentVariables[variable.Key] = variable.Value;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var exited = new TaskCompletionSource<bool>();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null && stderr.Length < 16000) stderr.AppendLine(e.Data); };
                proc.Exited += (s, e) => exited.TrySetResult(true);

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                {
                    await exited.Task.ConfigureAwait(false);
                }
                proc.WaitForExit();
                ct.ThrowIfCancellationRequested();

                return new ProcessResult { ExitCode = proc.ExitCode, StdOut = stdout.ToString(), StdErr = stderr.ToString().Trim() };
            }
        }
    }
}
