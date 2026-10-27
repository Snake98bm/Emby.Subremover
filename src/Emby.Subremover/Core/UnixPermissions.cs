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

        /// <returns>null when everything was copied (or on Windows), otherwise a note about what couldn't be.</returns>
        public static string CopyFrom(string source, string target)
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

            // Only root can give a file away to another user; changing to a group Emby belongs to may still work.
            var chown = Run("chown", "--reference=" + FfmpegArgs.Quote(source) + " " + FfmpegArgs.Quote(target));
            if (chown != null)
            {
                note = (note == null ? "" : note + "; ") + "could not copy owner/group: " + chown;
            }
            return note;
        }

        private static string Run(string exe, string arguments)
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
                    var error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0 ? null : error.Trim();
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
