using System;
using System.IO;
using System.Text;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Mends the list of SubRemover's subtitle files from 0.7.0 to 0.7.9-alpha.4: Emby's JSON reader doesn't decode the
    /// text in a saved set, so every save doubled each backslash in those paths. After enough runs the state file grew
    /// to gigabytes and the settings page took ages to open.
    /// </summary>
    public static class SavedPaths
    {
        /// <summary>A state file bigger than this is read through once to shrink its spoiled paths.</summary>
        public const long SpoiledSize = 20L * 1024 * 1024;

        /// <summary>"\\\\\\\\nas\\\\Emby" (read back as text) becomes "\\nas\Emby": a path never has two backslashes in a
        /// row except at the start of a network path.</summary>
        public static string Mend(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') < 0) return path;
            var sb = new StringBuilder(path.Length);
            var i = 0;
            while (i < path.Length)
            {
                if (path[i] != '\\')
                {
                    sb.Append(path[i++]);
                    continue;
                }
                var start = i;
                while (i < path.Length && path[i] == '\\') i++;
                sb.Append(start == 0 && i - start >= 2 ? @"\\" : @"\");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Rewrites a big state file with every run of backslashes in its JSON text cut back to what a path has: four
        /// (an escaped "\\") where a string starts, two (an escaped "\") inside one. The new file is only used when
        /// <paramref name="isReadable"/> accepts it; the old one is kept next to it as ".before-repair".
        /// </summary>
        /// <returns>True when the file was shrunk.</returns>
        public static bool ShrinkIfSpoiled(string file, Func<string, bool> isReadable)
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length < SpoiledSize) return false;
                var temp = file + ".repair";
                using (var reader = new StreamReader(file, new UTF8Encoding(false), true, 1 << 20))
                using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false), 1 << 20))
                {
                    ShrinkJson(reader, writer);
                }
                if (new FileInfo(temp).Length >= new FileInfo(file).Length || !isReadable(temp))
                {
                    File.Delete(temp);
                    return false;
                }
                var kept = file + ".before-repair";
                if (File.Exists(kept)) File.Delete(kept);
                File.Replace(temp, file, kept);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ShrinkJson(TextReader reader, TextWriter writer)
        {
            var previous = -1;
            var c = reader.Read();
            while (c >= 0)
            {
                if (c != '\\')
                {
                    writer.Write((char)c);
                    previous = c;
                    c = reader.Read();
                    continue;
                }
                long run = 0;
                while (c == '\\')
                {
                    run++;
                    c = reader.Read();
                }
                // Odd: the last one escapes the next character (\" or \n); it stays.
                var escapesNext = run % 2 == 1;
                var pairs = run / 2;
                var keepPairs = pairs == 0 ? 0 : previous == '"' && pairs >= 2 ? 2 : 1;
                writer.Write(new string('\\', (int)(keepPairs * 2 + (escapesNext ? 1 : 0))));
                previous = '\\';
                if (escapesNext && c >= 0)
                {
                    writer.Write((char)c);
                    previous = c == '"' ? -1 : c; // an escaped quote doesn't start a string
                    c = reader.Read();
                }
            }
        }
    }
}
