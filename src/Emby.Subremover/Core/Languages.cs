using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Subremover.Core
{
    public class Language
    {
        public Language(string code, string name, params string[] aliases)
        {
            Code = code;
            Name = name;
            Aliases = aliases;
        }

        /// <summary>ISO 639-2/B code, the form Matroska files and Emby normally use.</summary>
        public string Code { get; }
        public string Name { get; }
        /// <summary>ISO 639-2/T and ISO 639-1 spellings that mean the same language.</summary>
        public string[] Aliases { get; }
    }

    public static class Languages
    {
        public static readonly IReadOnlyList<Language> All = new List<Language>
        {
            new Language("afr", "Afrikaans", "af"),
            new Language("alb", "Albanian", "sqi", "sq"),
            new Language("ara", "Arabic", "ar"),
            new Language("arm", "Armenian", "hye", "hy"),
            new Language("aze", "Azerbaijani", "az"),
            new Language("baq", "Basque", "eus", "eu"),
            new Language("ben", "Bengali", "bn"),
            new Language("bos", "Bosnian", "bs"),
            new Language("bul", "Bulgarian", "bg"),
            new Language("bur", "Burmese", "mya", "my"),
            new Language("cat", "Catalan", "ca"),
            new Language("chi", "Chinese", "zho", "zh", "cmn"),
            new Language("hrv", "Croatian", "hr", "scr"),
            new Language("cze", "Czech", "ces", "cs"),
            new Language("dan", "Danish", "da"),
            new Language("dut", "Dutch", "nld", "nl"),
            new Language("eng", "English", "en"),
            new Language("est", "Estonian", "et"),
            new Language("fil", "Filipino", "tgl", "tl"),
            new Language("fin", "Finnish", "fi"),
            new Language("fre", "French", "fra", "fr"),
            new Language("glg", "Galician", "gl"),
            new Language("geo", "Georgian", "kat", "ka"),
            new Language("ger", "German", "deu", "de"),
            new Language("gre", "Greek", "ell", "el"),
            new Language("guj", "Gujarati", "gu"),
            new Language("heb", "Hebrew", "he", "iw"),
            new Language("hin", "Hindi", "hi"),
            new Language("hun", "Hungarian", "hu"),
            new Language("ice", "Icelandic", "isl", "is"),
            new Language("ind", "Indonesian", "id", "in"),
            new Language("gle", "Irish", "ga"),
            new Language("ita", "Italian", "it"),
            new Language("jpn", "Japanese", "ja"),
            new Language("kan", "Kannada", "kn"),
            new Language("kaz", "Kazakh", "kk"),
            new Language("khm", "Khmer", "km"),
            new Language("kor", "Korean", "ko"),
            new Language("lao", "Lao", "lo"),
            new Language("lat", "Latin", "la"),
            new Language("lav", "Latvian", "lv"),
            new Language("lit", "Lithuanian", "lt"),
            new Language("mac", "Macedonian", "mkd", "mk"),
            new Language("may", "Malay", "msa", "ms"),
            new Language("mal", "Malayalam", "ml"),
            new Language("mar", "Marathi", "mr"),
            new Language("mon", "Mongolian", "mn"),
            new Language("nep", "Nepali", "ne"),
            new Language("nor", "Norwegian", "no", "nob", "nb", "nno", "nn"),
            new Language("per", "Persian", "fas", "fa"),
            new Language("pol", "Polish", "pl"),
            new Language("por", "Portuguese", "pt", "pob"),
            new Language("pan", "Punjabi", "pa"),
            new Language("rum", "Romanian", "ron", "ro", "mol"),
            new Language("rus", "Russian", "ru"),
            new Language("srp", "Serbian", "sr", "scc"),
            new Language("sin", "Sinhala", "si"),
            new Language("slo", "Slovak", "slk", "sk"),
            new Language("slv", "Slovenian", "sl"),
            new Language("spa", "Spanish", "es", "spl"),
            new Language("swa", "Swahili", "sw"),
            new Language("swe", "Swedish", "sv"),
            new Language("tam", "Tamil", "ta"),
            new Language("tel", "Telugu", "te"),
            new Language("tha", "Thai", "th"),
            new Language("tur", "Turkish", "tr"),
            new Language("ukr", "Ukrainian", "uk"),
            new Language("urd", "Urdu", "ur"),
            new Language("vie", "Vietnamese", "vi"),
            new Language("wel", "Welsh", "cym", "cy"),
            new Language("yue", "Cantonese"),
        };

        private static readonly Dictionary<string, string> CanonicalByAnySpelling = BuildLookup();

        private static Dictionary<string, string> BuildLookup()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lang in All)
            {
                map[lang.Code] = lang.Code;
                foreach (var alias in lang.Aliases) map[alias] = lang.Code;
            }
            return map;
        }

        /// <summary>"deu", "de" and "GER" all become "ger"; unknown codes are just lower-cased; empty becomes "und".</summary>
        public static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "und";
            var trimmed = code.Trim();
            // Some files use regional tags like "en-US" or "pt_BR".
            var primary = trimmed.Split('-', '_')[0];
            if (CanonicalByAnySpelling.TryGetValue(trimmed, out var canonical)) return canonical;
            if (CanonicalByAnySpelling.TryGetValue(primary, out canonical)) return canonical;
            return trimmed.ToLowerInvariant();
        }

        public static string NameOf(string code) =>
            All.FirstOrDefault(l => l.Code == code)?.Name ?? code;
    }
}
