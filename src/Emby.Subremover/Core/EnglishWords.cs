using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

// The same file is in AudioRemover (Core/EnglishWords.cs), with its own namespace. Keep the two copies alike.
namespace Emby.Subremover.Core
{
    /// <summary>English words (12dicts 2of12inf, see THIRD-PARTY-NOTICES.txt), lowercase, apostrophes as '.</summary>
    public static class EnglishWords
    {
        private static readonly Lazy<HashSet<string>> Words = new Lazy<HashSet<string>>(Load);

        // The list has no contractions and no "I".
        private static readonly HashSet<string> Extra = new HashSet<string>(StringComparer.Ordinal)
        {
            "i", "i'm", "i've", "i'll", "i'd", "don't", "doesn't", "didn't", "can't", "couldn't", "won't", "wouldn't", "shouldn't",
            "isn't", "aren't", "wasn't", "weren't", "haven't", "hasn't", "hadn't", "mustn't", "needn't", "ain't", "shan't",
            "let's", "y'all", "ma'am", "o'clock", "'em", "'cause", "'til"
        };

        private static readonly HashSet<string> ContractionStarts = new HashSet<string>(StringComparer.Ordinal)
        {
            "you", "we", "they", "he", "she", "it", "that", "there", "what", "who", "where", "how", "here", "this", "when", "why"
        };

        public static bool Contains(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            var w = word.ToLowerInvariant().Replace('’', '\'');
            if (Words.Value.Contains(w) || Extra.Contains(w)) return true;
            // "you're", "they've", "we'll", "she'd", "that'll".
            var m = Regex.Match(w, @"^([a-z]+)'(re|ve|ll|d)$");
            if (m.Success) return ContractionStarts.Contains(m.Groups[1].Value);
            // Possessives and dropped g's: "Frank's", "Ripley's", "goin'".
            if (w.EndsWith("'s", StringComparison.Ordinal)) return Words.Value.Contains(w.Substring(0, w.Length - 2));
            if (w.EndsWith("in'", StringComparison.Ordinal)) return Words.Value.Contains(w.Substring(0, w.Length - 1) + "g");
            return false;
        }

        private static HashSet<string> Load()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var name = typeof(EnglishWords).Namespace.Replace(".Core", "") + ".english-words.txt.gz";
            using (var resource = typeof(EnglishWords).Assembly.GetManifestResourceStream(name))
            {
                if (resource == null) return set;
                using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length > 0) set.Add(line);
                    }
                }
            }
            return set;
        }
    }
}
