using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// The last result of each button task (hash-matched subtitles, ads, timing), kept in "Subremover.last-results.txt"
    /// in Emby's plugin settings folder, so the settings page still shows when it last ran and what it found after Emby
    /// restarts. One line per task: name, a tab, the result.
    /// </summary>
    public static class LastResults
    {
        public const string FileName = "Subremover.last-results.txt";
        private static readonly object Lock = new object();
        private static string _folder;

        /// <summary>Called by each task when Emby creates it.</summary>
        public static void UseFolder(string folder)
        {
            if (!string.IsNullOrEmpty(folder)) _folder = folder;
        }

        public static void Save(string task, string result)
        {
            if (_folder == null || string.IsNullOrEmpty(result)) return;
            lock (Lock)
            {
                try
                {
                    var all = ReadAll();
                    all[task] = result.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
                    var path = Path.Combine(_folder, FileName);
                    File.WriteAllText(path + ".tmp", string.Concat(all.Select(kv => kv.Key + "\t" + kv.Value + "\n")), Encoding.UTF8);
                    if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                    else File.Move(path + ".tmp", path);
                }
                catch (Exception)
                {
                    // Only for the settings page: the run's log has the result too.
                }
            }
        }

        /// <summary>The task's last saved result, or null when it never ran (or the file can't be read).</summary>
        public static string Load(string task)
        {
            if (_folder == null) return null;
            lock (Lock)
            {
                try { return ReadAll().TryGetValue(task, out var result) ? result : null; }
                catch (Exception) { return null; }
            }
        }

        private static Dictionary<string, string> ReadAll()
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = Path.Combine(_folder, FileName);
            if (!File.Exists(path)) return all;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                var tab = line.IndexOf('\t');
                if (tab > 0) all[line.Substring(0, tab)] = line.Substring(tab + 1);
            }
            return all;
        }
    }
}
