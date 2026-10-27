using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Emby.Subremover.Core
{
    public class ReplaceResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string BackupPath { get; set; }

        /// <summary>Set only when this call created the backup (an older backup is never overwritten).</summary>
        public BackupEntry NewBackup { get; set; }
    }

    public static class BackupManager
    {
        /// <summary>Puts <paramref name="replacement"/> in place of <paramref name="original"/>, backing the original up first.</summary>
        public static ReplaceResult Replace(string original, string replacement, BackupMode mode, string backupFolder)
        {
            var backup = BackupPaths.For(original, mode, backupFolder);
            var created = File.GetCreationTimeUtc(original);
            var permissionNote = UnixPermissions.CopyFrom(original, replacement);

            // An existing backup is from an earlier run and is closer to the true original, so keep it.
            var newBackup = backup != null && !File.Exists(backup);

            try
            {
                if (mode == BackupMode.NextToOriginal && newBackup)
                {
                    File.Move(original, backup);
                }
                else
                {
                    if (mode == BackupMode.BackupFolder && newBackup)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup));
                        File.Copy(original, backup);
                    }
                    File.Delete(original);
                }
            }
            catch (Exception ex)
            {
                if (mode == BackupMode.BackupFolder && newBackup) TryDelete(backup);
                TryDelete(replacement);
                return new ReplaceResult { Message = "could not back up the original, it was left unchanged: " + ex.Message };
            }

            try
            {
                File.Move(replacement, original);
            }
            catch (Exception ex)
            {
                if (backup != null && File.Exists(backup) && !File.Exists(original))
                {
                    if (mode == BackupMode.NextToOriginal && newBackup) File.Move(backup, original);
                    else File.Copy(backup, original);
                    return new ReplaceResult { Message = "could not move the new file into place (" + ex.Message + "); original restored from backup" };
                }
                return new ReplaceResult { Message = "could not move the new file into place (" + ex.Message + "); the new file is still at " + replacement };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try { File.SetCreationTimeUtc(original, created); } catch { }
            }

            return new ReplaceResult
            {
                Success = true,
                Message = permissionNote,
                BackupPath = backup,
                NewBackup = newBackup ? new BackupEntry { OriginalPath = original, BackupPath = backup, CreatedUtc = DateTime.UtcNow } : null
            };
        }

        public static void Restore(BackupEntry entry)
        {
            var original = entry.OriginalPath;
            if (entry.BackupPath == original + BackupPaths.SideBySideSuffix)
            {
                if (File.Exists(original)) File.Delete(original);
                File.Move(entry.BackupPath, original);
                return;
            }

            // Backup is in another folder (maybe another drive): copy next to the original first so a failed copy never loses the current file.
            var temp = original + Leftovers.RestoreSuffix;
            File.Copy(entry.BackupPath, temp, true);
            if (File.Exists(original)) File.Delete(original);
            File.Move(temp, original);
            File.Delete(entry.BackupPath);
        }

        public static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
