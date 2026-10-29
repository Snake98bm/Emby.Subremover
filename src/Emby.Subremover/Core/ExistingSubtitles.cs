using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Subtitle files next to a video that SubRemover didn't make, such as ones Emby downloaded ("Movie.en.srt").
    /// </summary>
    public static class ExistingSubtitles
    {
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".ass", ".ssa", ".sub", ".vtt", ".smi", ".sup"
        };

        // OCR files carry one of these markers; older SubRemover versions made them before it kept a list of its files.
        private static readonly HashSet<string> OcrMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pgs", "dvd", "dvb", "xsub" };

        /// <returns>Language code -> file name, for each language that has such a file.</returns>
        public static Dictionary<string, string> Find(string videoPath, ICollection<string> madeBySubRemover)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dir = Path.GetDirectoryName(videoPath);
            var name = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return found;

            foreach (var file in Directory.EnumerateFiles(dir, name + ".*"))
            {
                if (!Extensions.Contains(Path.GetExtension(file)) || madeBySubRemover.Contains(file)) continue;
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length <= name.Length || !stem.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) continue;

                var tokens = stem.Substring(name.Length + 1).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Any(OcrMarkers.Contains)) continue;
                var language = tokens.Where(Languages.IsKnown).Select(Languages.Normalize).FirstOrDefault();
                if (language != null && !found.ContainsKey(language)) found[language] = Path.GetFileName(file);
            }
            return found;
        }
    }
}
