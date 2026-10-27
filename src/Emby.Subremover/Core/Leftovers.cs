using System;
using System.Collections.Generic;
using System.IO;

namespace Emby.Subremover.Core
{
    /// <summary>Removes temporary files left next to a video when Emby stopped while Subremover was working on it.</summary>
    public static class Leftovers
    {
        public const string TempSuffix = ".subremover-tmp";
        public const string RestoreSuffix = ".subremover-restore";

        /// <returns>The files that were deleted.</returns>
        public static List<string> Clean(string videoPath)
        {
            var deleted = new List<string>();
            var dir = Path.GetDirectoryName(videoPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return deleted;

            var name = Path.GetFileNameWithoutExtension(videoPath);
            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                var ours = fileName.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)
                           || fileName.EndsWith(RestoreSuffix, StringComparison.OrdinalIgnoreCase);
                if (!ours || !fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Delete(file);
                    deleted.Add(file);
                }
                catch
                {
                }
            }
            return deleted;
        }
    }
}
