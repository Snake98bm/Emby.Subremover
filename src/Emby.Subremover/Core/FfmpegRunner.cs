using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        /// <summary>The program never ran: the system refused to start it (StdErr says why).</summary>
        public bool StartFailed { get; set; }

        /// <summary>
        /// Why a process couldn't start, with what can be checked from here. On Linux .NET reports a working folder it
        /// can't enter and a program it can't run with the same "No such file or directory", so both are looked at.
        /// </summary>
        internal static ProcessResult CouldNotStart(string exe, string workingDirectory, Exception ex)
        {
            string Exists(Func<bool> check) { try { return check() ? "found" : "NOT found"; } catch (Exception) { return "can't tell"; } }
            var where = string.IsNullOrEmpty(workingDirectory) ? "" : $" in {workingDirectory} (folder {Exists(() => Directory.Exists(workingDirectory))})";
            return new ProcessResult
            {
                ExitCode = -1,
                StartFailed = true,
                StdErr = $"couldn't start {exe} (program {Exists(() => File.Exists(exe))}){where}: {ex.Message}"
            };
        }
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

        /// <summary>
        /// Runs ffmpeg with its output going to stdout as raw bytes (e.g. "-f s16le pipe:1"), which <paramref name="read"/>
        /// reads to the end. The exit code and error text come back as usual; StdOut stays empty.
        /// </summary>
        public async Task<(ProcessResult Result, T Value)> RunFfmpegBinary<T>(IList<string> args, Func<Stream, T> read, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(_ffmpegPath(), FfmpegArgs.ToCommandLine(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var variable in _environment()) psi.EnvironmentVariables[variable.Key] = variable.Value;

            var stderr = new StringBuilder();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null && stderr.Length < 16000) stderr.AppendLine(e.Data); };
                try { proc.Start(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
                {
                    return (ProcessResult.CouldNotStart(psi.FileName, null, ex), default(T));
                }
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
                proc.BeginErrorReadLine();
                T value;
                using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                {
                    value = await Task.Run(() => read(proc.StandardOutput.BaseStream), ct).ConfigureAwait(false);
                    proc.WaitForExit();
                }
                ct.ThrowIfCancellationRequested();
                return (new ProcessResult { ExitCode = proc.ExitCode, StdOut = string.Empty, StdErr = stderr.ToString().Trim() }, value);
            }
        }

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

                // A program or working folder the system can't use: a failure with the reason, not an error that stops the video.
                try { proc.Start(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
                {
                    return ProcessResult.CouldNotStart(exe, workingDirectory, ex);
                }
                // Background work: playback and Emby's own transcoding come first. (On Linux this is a higher nice value.)
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
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
