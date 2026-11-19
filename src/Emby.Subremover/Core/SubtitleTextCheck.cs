using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// The same file is in AudioRemover (Core/SubtitleTextCheck.cs), with its own namespace. Keep the two copies alike.
namespace Emby.Subremover.Core
{
    /// <summary>
    /// Whether a downloaded subtitle really reads as the language it was asked for. Catches a file in another language
    /// tagged as this one, and (for English) a machine-translated file that keeps another language's words mixed into
    /// its lines ("My younger zanaki cue-Gregorio"): a real subtitle has such lines only here and there (a name, a
    /// line of Latin), a garbled one all the way through.
    /// </summary>
    public static class SubtitleTextCheck
    {
        /// <summary>A file with fewer lines to judge isn't judged.</summary>
        public const int MinLines = 100;

        /// <summary>Share of lines with at least two English words and one word that isn't English: above it, garbled.
        /// Good subtitles measured 1-6%, a machine-translated one 19%.</summary>
        public const double MaxMixedShare = 0.12;

        private static readonly Regex Tag = new Regex(@"<[^>]+>|\{[^}]*\}", RegexOptions.CultureInvariant);
        private static readonly Regex Word = new Regex(@"[A-Za-z][A-Za-z'’]*", RegexOptions.CultureInvariant);

        /// <summary>Why the text isn't the wanted language, or null when it is (or there's too little to tell).</summary>
        public static string WhyNot(string srtText, string language)
        {
            if (string.IsNullOrWhiteSpace(srtText) || string.IsNullOrWhiteSpace(language)) return null;
            var lang = Languages.IsKnown(language) ? Languages.Normalize(language) : language.ToLowerInvariant();

            var guess = LanguageGuesser.Guess(srtText);
            if (LanguageGuesser.ContradictsTag(guess, lang))
                return $"its text reads as {guess.Language}, not {lang}";

            if (lang != "eng") return null;
            var share = MixedLineShare(srtText, out var lines);
            if (lines >= MinLines && share > MaxMixedShare)
                return $"{share:P0} of its lines mix in words that aren't English, so it's probably machine-translated or garbled";
            return null;
        }

        /// <summary>Of the text lines with three or more lower-case words, the share with at least two English words and
        /// at least one that isn't. Capitalised words are left out (names).</summary>
        public static double MixedLineShare(string srtText, out int lines)
        {
            lines = 0;
            var mixed = 0;
            foreach (var raw in srtText.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Contains("-->")) continue;
                var words = Word.Matches(Tag.Replace(raw, " ")).Cast<Match>().Select(m => m.Value)
                    .Where(w => w.Length >= 3 && char.IsLower(w[0])).ToList();
                if (words.Count < 3) continue;
                lines++;
                var known = words.Count(IsEnglish);
                if (known >= 2 && known < words.Count) mixed++;
            }
            return lines == 0 ? 0 : (double)mixed / lines;
        }

        /// <summary>A subtitle file's text: UTF-8 or UTF-16 by its byte order mark, UTF-8 when it is valid UTF-8, else
        /// Windows-1252 (Latin-1 where that isn't available), which is how most older downloads are written.</summary>
        public static string ReadText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { }
            try { return Encoding.GetEncoding(1252).GetString(bytes); }
            catch (Exception) { return Encoding.GetEncoding(28591).GetString(bytes); }
        }

        // OCR'd subtitles often read "I" as "l" ("l'm", "lt's"); those are English words misread, not foreign ones.
        private static bool IsEnglish(string word) =>
            EnglishWords.Contains(word) || (word[0] == 'l' && EnglishWords.Contains("i" + word.Substring(1)));
    }
}
