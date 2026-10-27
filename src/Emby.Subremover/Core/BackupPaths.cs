using System.IO;

namespace Emby.Subremover.Core
{
    public enum BackupMode
    {
        NextToOriginal,
        BackupFolder,
        None
    }

    public static class BackupPaths
    {
        // Emby ignores unknown extensions, so the backup won't show up as a second copy of the movie.
        public const string SideBySideSuffix = ".subremover.bak";

        public static string For(string originalPath, BackupMode mode, string backupFolder)
        {
            switch (mode)
            {
                case BackupMode.NextToOriginal:
                    return originalPath + SideBySideSuffix;
                case BackupMode.BackupFolder:
                    return Path.Combine(backupFolder, RelativeFromRoot(originalPath));
                default:
                    return null;
            }
        }

        // "D:\Movies\A\a.mkv" -> "D\Movies\A\a.mkv", "/mnt/media/a.mkv" -> "mnt/media/a.mkv", "\\nas\share\a.mkv" -> "nas\share\a.mkv"
        internal static string RelativeFromRoot(string path)
        {
            var trimmed = path.TrimStart('\\', '/');
            if (trimmed.Length >= 2 && trimmed[1] == ':')
            {
                trimmed = trimmed[0] + trimmed.Substring(2);
            }
            return trimmed.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
