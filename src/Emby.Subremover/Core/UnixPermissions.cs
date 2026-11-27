using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Gives a new file the same permission bits (and, where allowed, owner and group) as the file it replaces,
    /// so other apps and shares keep their access on Linux, macOS and NAS systems.
    /// </summary>
    public static class UnixPermissions
    {
        // File.Get/SetUnixFileMode exist from .NET 7, which Emby runs on, but not in netstandard2.0, so call them by reflection.
        private static readonly MethodInfo GetMode = typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) });
        private static readonly MethodInfo SetMode = typeof(File).GetMethods()
            .FirstOrDefault(m => m.Name == "SetUnixFileMode" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(string));

        /// <summary>Runs a program: its exit code, standard output and standard error.</summary>
        internal delegate (int ExitCode, string Output, string Error) Runner(string exe, string arguments);

        /// <returns>null when everything was copied (or on Windows), otherwise a note about what couldn't be.</returns>
        public static string CopyFrom(string source, string target) => CopyFrom(source, target, RunProcess);

        internal static string CopyFrom(string source, string target, Runner run)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            string note = null;
            try
            {
                if (GetMode != null && SetMode != null)
                {
                    SetMode.Invoke(null, new[] { target, GetMode.Invoke(null, new object[] { source }) });
                }
                else
                {
                    note = "this .NET version can't copy file permissions";
                }
            }
            catch (Exception ex)
            {
                note = "could not copy permissions: " + (ex.InnerException ?? ex).Message;
            }

            // The new file usually has the original's owner and group already, and on network shares (CIFS/SMB, some NFS)
            // chown fails even then, so it's only tried when they differ.
            var before = Owner(source, run);
            var after = Owner(target, run);
            if (before != null && before == after) return note;

            // Only root can give a file away to another user; changing to a group Emby belongs to may still work.
            // By ids when stat gave them: BusyBox's chown (Emby's Docker image) has no --reference.
            var chown = run("chown", (before ?? "--reference=" + FfmpegArgs.Quote(source)) + " " + FfmpegArgs.Quote(target));
            if (chown.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(chown.Error) ? "chown exited with " + chown.ExitCode : chown.Error.Trim();
                note = (note == null ? "" : note + "; ") +
                       $"the file was updated, but it keeps owner/group {after ?? "?"} instead of the original's {before ?? "?"} ({error})";
            }
            return note;
        }

        /// <summary>"1000:100" (user and group ids), or null when it can't be told (e.g. macOS's stat).</summary>
        private static string Owner(string path, Runner run)
        {
            var stat = run("stat", "-c %u:%g " + FfmpegArgs.Quote(path));
            var owner = stat.Output?.Trim();
            return stat.ExitCode == 0 && !string.IsNullOrEmpty(owner) && owner.Contains(":") ? owner : null;
        }

        private static (int, string, string) RunProcess(string exe, string arguments)
        {
            try
            {
                using (var proc = Process.Start(new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }))
                {
                    var errorTask = proc.StandardError.ReadToEndAsync();
                    var output = proc.StandardOutput.ReadToEnd();
                    var error = errorTask.Result;
                    proc.WaitForExit(10000);
                    return (proc.ExitCode, output, error);
                }
            }
            catch (Exception ex)
            {
                return (-1, null, ex.Message);
            }
        }
    }
}
