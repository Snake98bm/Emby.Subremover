using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// What "Remove ads from subtitle files" takes out of an .srt file: lines with an entry from the "Text to remove" list
    /// (subtitle sites, ad wording), "Subtitles by" / "Synced by" credits, and, if asked, TV captioning credits. A cue with
    /// such a line goes as a whole: ads like "Support us and become VIP member / to remove all ads from ..." are split over
    /// two lines, and only one of them names the site.
    /// </summary>
    public sealed class SubtitleAdRules
    {
        /// <summary>The list the settings page starts with: the sites and ad wording found in real subtitle files.</summary>
        public static readonly string DefaultList = string.Join("\n", new[]
        {
            "opensubtitles", "open subtitles", "open-subtitles", "osdb.link", "addic7ed", "admitme", "flixify", "allsubs",
            "tvsubtitles", "subscene", "seriessub", "podnapisi", "titlovi", "napiprojekt", "subdl", "subsmax", "uksubtitles",
            "isubdb", "yify", "yts", "rarbg", "tbi-torrents", "advertise your product", "become VIP member", "remove all ads",
            "please rate this subtitle", @"/downloaded from\s+(www\.|https?:|[\w-]+\.(com|org|net|app|tv))/", @"/admit\w*\.app/",
            "4kvod", "filebot", "thepiratebay", "tusubtitulo", "swesub", "forom.com"
        });

        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
        private const int MaxEntries = 300;
        private const int MaxEntryLength = 200;

        // "Subtitles by X", "Sync & corrections by X", "== sync, corrected by X ==", "Synchro: X".
        // Only at the start of a line, so dialogue like "He was corrected by his teacher" stays.
        internal static readonly Regex Credit = new Regex(
            @"^(?:subtitles?|subs|sub-titles|subtitled|transcript(?:ion)?|corrections?|resync(?:ed)?|" +
            @"re-sync(?:ed)?|sync(?:ed|hronized|hronised|hro)?|ocr)\b[^.!?]{0,40}?\bby\b" +
            @"|^(?:synchro|syncro|transcript)\s*:|^(?:subtitles?|subs)\s*:" +
            // "Precisely Synchronized by X", "... Syncro: X": anywhere in the line, as dialogue never says it.
            @"|\b(?:re-?)?(?:synchroni[sz]ed|synced|resynced)\s+by\b|\bsyncro\s*:|\bsubtitles?\s+by\b",
            Options, RegexTimeout);

        // "Encoded by X", "Created and Encoded by X", "Translated by X", "Final edit by X": words dialogue uses too. Dialogue
        // has them mostly as a sentence carried over from the line before ("...the downdraft / created by a helicopter,"), so
        // they only count with a capital letter and a name after "by", not "the", "a" or "what's".
        internal static readonly Regex WeakCredit = new Regex(
            @"^(?:translation|translated|timing|timed|ripped|encoded|corrected|edited|edits?|created|shared|uploaded|provided|" +
            @"initial\s+edits?|final\s+edit)\b[^.!?]{0,40}?\bby\b",
            Options, RegexTimeout);

        private static readonly Regex NotAName = new Regex(
            @"^\W*(?:the|a|an|what|what's|whatever|whoever|doing|being|using|making|his|her|him|their|them|" +
            @"our|us|my|me|your|you|its|it|this|that|these|those|some|one|any|all|each|every|no|hand|far|accident|mistake)\b",
            RegexOptions.CultureInvariant, RegexTimeout); // lowercase only: "by The Subs Team" and "by A.B. Someone" are names

        private static bool IsWeakCredit(string start)
        {
            if (start.Length == 0 || !char.IsUpper(start[0])) return false;
            var m = WeakCredit.Match(start);
            return m.Success && !NotAName.IsMatch(start.Substring(m.Index + m.Length));
        }

        // "Captioned by Media Access Group at WGBH", "Captioning sponsored by ...", "Captioning made possible by ...".
        internal static readonly Regex CaptionCredit = new Regex(
            @"^(?:closed[\s-]+)?caption(?:s|ed|ing)?\b[^.!?]{0,60}?\b(?:by|sponsored|possible|provided|performed)\b" +
            @"|media access group|national captioning institute|\bncicap\b|\bwgbh\b",
            Options, RegexTimeout);

        // What's in front of a credit: "- ", "== ", "[", "~ ".
        private static readonly Regex LeadingMarks = new Regex(@"^[\s\-=~*#\[\(""'♪>|_.]+", RegexOptions.CultureInvariant);

        // Lines found in every film: an entry that takes any of them would cut dialogue.
        private static readonly string[] OrdinaryLines =
        {
            "I love you.", "Okay.", "What are you doing?", "Thank you.", "Come on, let's go.", "- Yes. - No.", "Where are you?",
            "I downloaded it from the internet.", "Did you see the website?", "We're going to the movies.", "Watch out!",
            "Hold on.", "Subtitles", "Can you support us?", "Rate it.", "Open the door!", "Sync your watches.", "a", "1"
        };

        private static readonly Regex NestedRepeat = new Regex(
            @"\((?:[^()\\]|\\.)*(?:[+*]|\{\d+,\d*\})(?:[^()\\]|\\.)*\)(?:[+*]|\{\d+,\d*\})", RegexOptions.CultureInvariant, RegexTimeout);

        private sealed class Rule
        {
            public string Entry;
            public Regex Regex;
            public bool SwitchedOff;
        }

        private readonly List<Rule> _rules = new List<Rule>();
        private readonly bool _credits;
        private readonly bool _captionCredits;

        public SubtitleAdRules(string list, bool credits = true, bool captionCredits = false)
        {
            _credits = credits;
            _captionCredits = captionCredits;
            var entries = Lines(list);
            var problems = new List<string>();
            if (entries.Count > MaxEntries)
            {
                problems.Add($"only the first {MaxEntries} entries are used; the other {entries.Count - MaxEntries} are skipped");
                entries = entries.Take(MaxEntries).ToList();
            }
            foreach (var entry in entries)
            {
                var problem = Build(entry, out var regex);
                if (problem != null) problems.Add($"'{(entry.Length <= 60 ? entry : entry.Substring(0, 57) + "...")}' {problem}");
                else _rules.Add(new Rule { Entry = entry, Regex = regex });
            }
            Problems = problems;
            Signature = string.Join("\n", entries) + $"\ncredits={credits}\ncaptions={captionCredits}";
        }

        /// <summary>Entries that are skipped, each with the reason, for the run log.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Changes whenever what's removed changes, so files checked before are checked again.</summary>
        public string Signature { get; }

        /// <summary>A list's lines, trimmed, each once.</summary>
        public static List<string> Lines(string text) =>
            (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private static bool IsRegex(string entry) => entry.Length > 2 && entry[0] == '/' && entry[entry.Length - 1] == '/';

        private static string Build(string entry, out Regex regex)
        {
            regex = null;
            if (entry.Length > MaxEntryLength) return $"is longer than {MaxEntryLength} characters";
            string pattern;
            if (IsRegex(entry))
            {
                pattern = entry.Substring(1, entry.Length - 2);
                if (NestedRepeat.IsMatch(pattern)) return "has a repeat inside a repeat, like (a+)+, which can take forever: write it without";
            }
            else
            {
                // Whole words only, so "yts" doesn't take "Bytes"; a space matches any gap.
                pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(entry).Replace(@"\ ", @"[\s._-]+") + @"(?![\p{L}\p{N}])";
            }
            try
            {
                regex = new Regex(pattern, Options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return "isn't a valid regular expression (" + ex.Message + ")";
            }
            try
            {
                if (regex.IsMatch(string.Empty)) return "also matches nothing at all, which would remove every line";
                var ordinary = OrdinaryLines.FirstOrDefault(regex.IsMatch);
                if (ordinary != null) return $"would remove ordinary dialogue like '{ordinary}'";
                regex.IsMatch(new string('a', 40) + "!");
                return null;
            }
            catch (RegexMatchTimeoutException)
            {
                return "is too slow";
            }
        }

        /// <summary>Why this subtitle line is an ad ("addic7ed", "credit", "captioning credit"), or null when it isn't.</summary>
        public string Match(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            foreach (var rule in _rules)
            {
                if (rule.SwitchedOff) continue;
                try
                {
                    if (rule.Regex.IsMatch(line)) return rule.Entry;
                }
                catch (RegexMatchTimeoutException)
                {
                    rule.SwitchedOff = true;
                }
            }
            var start = LeadingMarks.Replace(line, "");
            try
            {
                if (_captionCredits && CaptionCredit.IsMatch(start)) return "captioning credit";
                // A caption credit is never taken for a release credit: it only goes with its own switch.
                if (_credits && !CaptionCredit.IsMatch(start) && (Credit.IsMatch(start) || IsWeakCredit(start))) return "credit";
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return null;
        }
    }

    /// <summary>One cue taken out of a subtitle file.</summary>
    public sealed class RemovedCue
    {
        public int Number { get; set; }
        public string Time { get; set; }
        public string Text { get; set; }
        public string Rule { get; set; }
    }

    public sealed class SrtCleanResult
    {
        public string Text { get; set; }
        public int Cues { get; set; }
        public List<RemovedCue> Removed { get; } = new List<RemovedCue>();

        /// <summary>The text of every cue that stays, with its position, for the suggestions.</summary>
        public List<(int Number, string Text)> Kept { get; } = new List<(int, string)>();
    }

    /// <summary>Takes the cues the rules call ads out of an .srt file's text; everything else stays exactly as it was.</summary>
    public static class SrtAdCleaner
    {
        private static readonly Regex Markup = new Regex(@"<[^>]*>|\{[^}]*\}", RegexOptions.CultureInvariant);
        private static readonly Regex Number = new Regex(@"^\s*\d+\s*$", RegexOptions.CultureInvariant);

        // Zero bytes and other control characters some files are padded with at the end: not text, so not matched or logged.
        private static readonly Regex Unprintable = new Regex(@"[\p{Cc}�]+", RegexOptions.CultureInvariant);

        /// <summary>A removed cue's text for the run log: on one line, and cut short if a broken file made it huge.</summary>
        public static string ForLog(string text)
        {
            text = Regex.Replace(Unprintable.Replace(text ?? string.Empty, " "), @"\s{2,}", " ").Trim();
            return text.Length <= 160 ? text : text.Substring(0, 157).TrimEnd() + "...";
        }

        private sealed class Cue
        {
            public List<string> Head = new List<string>();  // the number, if any
            public string Timing;                           // null: not a cue (text before the first one)
            public List<string> Text = new List<string>();
        }

        public static SrtCleanResult Clean(string srt, SubtitleAdRules rules)
        {
            srt = srt ?? string.Empty;
            var newline = srt.Contains("\r\n") ? "\r\n" : "\n";
            // Whatever the file ends with after the last cue ("\r\n", "\r\n\r\n", nothing) stays.
            var ending = srt.Substring(srt.TrimEnd('\r', '\n', ' ', '\t').Length);
            var lines = srt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // Blocks between blank lines. A block without a timing line after a cue belongs to that cue (a blank line inside
            // its text).
            var cues = new List<Cue>();
            var block = new List<string>();
            void EndBlock()
            {
                if (block.Count == 0) return;
                var t = block.FindIndex(l => l.Contains("-->"));
                if (t < 0 && cues.Count > 0 && cues[cues.Count - 1].Timing != null)
                {
                    cues[cues.Count - 1].Text.Add(string.Empty);
                    cues[cues.Count - 1].Text.AddRange(block);
                }
                else if (t < 0) cues.Add(new Cue { Head = new List<string>(block) });
                else cues.Add(new Cue { Head = block.Take(t).ToList(), Timing = block[t], Text = block.Skip(t + 1).ToList() });
                block = new List<string>();
            }
            foreach (var line in lines)
            {
                if (line.Trim().Length == 0) EndBlock();
                else block.Add(line);
            }
            EndBlock();

            var result = new SrtCleanResult();
            var kept = new List<Cue>();
            var position = 0;
            foreach (var cue in cues)
            {
                if (cue.Timing == null) { kept.Add(cue); continue; }
                position++;
                var plain = cue.Text.Select(l => Unprintable.Replace(Markup.Replace(l, ""), "").Trim()).Where(l => l.Length > 0).ToList();
                var rule = plain.Select(rules.Match).FirstOrDefault(r => r != null);
                if (rule != null)
                {
                    result.Removed.Add(new RemovedCue
                    {
                        Number = position,
                        Time = cue.Timing.Split(new[] { "-->" }, StringSplitOptions.None)[0].Trim(),
                        Text = string.Join(" / ", plain),
                        Rule = rule
                    });
                    continue;
                }
                result.Kept.Add((position, string.Join(" ", plain)));
                kept.Add(cue);
            }
            result.Cues = position;
            if (result.Removed.Count == 0)
            {
                result.Text = srt;
                return result;
            }

            // Numbered cues are numbered again from 1, as players expect.
            var n = 0;
            var renumber = kept.Where(c => c.Timing != null).All(c => c.Head.Count == 1 && Number.IsMatch(c.Head[0]));
            var output = kept.Select(c =>
            {
                var head = c.Head;
                if (c.Timing != null && renumber) head = new List<string> { (++n).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                return string.Join(newline, head.Concat(c.Timing != null ? new[] { c.Timing } : new string[0]).Concat(c.Text));
            });
            result.Text = string.Join(newline + newline, output) + ending;
            return result;
        }

        // ---- the file itself, read and written back in the encoding it had

        public sealed class SrtFile
        {
            public string Text;
            public Encoding Encoding;
            public byte[] Preamble;
        }

        public static SrtFile Read(string path) => Decode(File.ReadAllBytes(path));

        /// <summary>
        /// UTF-8 or UTF-16 with a byte order mark, UTF-8 without one, and anything else (Windows-1252, Korean, Polish...)
        /// byte for byte as Latin-1, so writing it back changes no byte but the cues taken out.
        /// </summary>
        public static SrtFile Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new SrtFile { Text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3), Encoding = new UTF8Encoding(false), Preamble = new byte[] { 0xEF, 0xBB, 0xBF } };
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new SrtFile { Text = new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2), Encoding = new UnicodeEncoding(false, false), Preamble = new byte[] { 0xFF, 0xFE } };
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new SrtFile { Text = new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2), Encoding = new UnicodeEncoding(true, false), Preamble = new byte[] { 0xFE, 0xFF } };
            try
            {
                var strict = new UTF8Encoding(false, true);
                return new SrtFile { Text = strict.GetString(bytes), Encoding = new UTF8Encoding(false), Preamble = new byte[0] };
            }
            catch (DecoderFallbackException)
            {
                var latin1 = Encoding.GetEncoding(28591);
                return new SrtFile { Text = latin1.GetString(bytes), Encoding = latin1, Preamble = new byte[0] };
            }
        }

        public static byte[] Encode(SrtFile file, string text) => file.Preamble.Concat(file.Encoding.GetBytes(text)).ToArray();
    }

    /// <summary>One line "Find ads in subtitle files" suggests for the "Text to remove" list.</summary>
    public sealed class SubtitleAdSuggestion
    {
        public string Entry { get; set; }
        public int Titles { get; set; }
        public string Example { get; set; }
        public List<string> TitleNames { get; set; } = new List<string>();
    }

    /// <summary>
    /// Looks through what the list leaves for ads it misses: a web address, an e-mail address, or a credit-like line at the
    /// start or end of the file, that turns up in at least two different films or shows. One show's own plug for its
    /// website ("Log on to discovery.com/myshow") is in one show only, so it's never suggested.
    /// </summary>
    public sealed class SubtitleAdSuggestions
    {
        public const int MinTitles = 2;
        public const int MostShown = 30;

        private static readonly Regex Domain = new Regex(
            @"(?<![\w.@-])(?:https?://)?(?:www\.)?((?:[a-z0-9][a-z0-9-]*\.)+(?:com|net|org|info|biz|co|io|me|to|tv|cc|ws|ru|xyz|club|link|site|online|app|pl|ro|gr|tk|pt|br|fr|de|it|es|nl|se|in|uk))(?![\w-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Email = new Regex(@"[\w.+-]+@([\w-]+\.)+[a-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CreditWords = new Regex(
            @"\b(?:by|subtitles?|subs|sync\w*|download\w*|visit|support|subscribe|rate|watch|uploaded|ripped|encoded|team|presents?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private sealed class Candidate
        {
            public string Example;
            public readonly HashSet<string> Titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Dictionary<string, Candidate> _found = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();

        /// <param name="title">The film or show the file belongs to.</param>
        public void Add(string title, SrtCleanResult result)
        {
            foreach (var (number, text) in result.Kept)
            {
                if (text.Length < 6 || text.Length > 160) continue;
                string entry = null;
                var domain = Domain.Match(text);
                if (domain.Success) entry = domain.Groups[1].Value.ToLowerInvariant();
                else if (Email.IsMatch(text)) entry = text;
                else if ((number <= 3 || number > result.Cues - 3) && CreditWords.IsMatch(text)) entry = text;
                if (entry == null) continue;
                lock (_lock)
                {
                    if (!_found.TryGetValue(entry, out var c)) _found[entry] = c = new Candidate { Example = text };
                    if (c.Titles.Count < 50) c.Titles.Add(title);
                }
            }
        }

        public List<SubtitleAdSuggestion> Waiting(SubtitleAdRules current)
        {
            lock (_lock)
            {
                return _found
                    .Where(f => f.Value.Titles.Count >= MinTitles)
                    // Takes nothing the list doesn't already, and passes the same checks as anything typed into it.
                    .Where(f => current.Match(f.Key) == null && new SubtitleAdRules(f.Key, false, false).Problems.Count == 0)
                    .OrderByDescending(f => f.Value.Titles.Count).ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(MostShown)
                    .Select(f => new SubtitleAdSuggestion
                    {
                        Entry = f.Key, Titles = f.Value.Titles.Count, Example = f.Value.Example,
                        TitleNames = f.Value.Titles.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()
                    })
                    .ToList();
            }
        }

        /// <summary>"My Show" for "\\nas\Shows\My Show\Season 01\x.srt", "My Movie (2020)" for "\\nas\Movies\My Movie (2020)\x.srt".</summary>
        public static string TitleOf(string path)
        {
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            var name = Path.GetFileName(folder);
            if (Regex.IsMatch(name ?? "", @"^(season|series|staffel|saison|temporada|specials?)\b", RegexOptions.IgnoreCase))
                name = Path.GetFileName(Path.GetDirectoryName(folder) ?? string.Empty);
            return string.IsNullOrEmpty(name) ? path : name;
        }
    }
}
