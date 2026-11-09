using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Fixes the misreads Tesseract makes most in English subtitles. Each rule only fires where the misread can't be
    /// a real word, e.g. "1" is turned into "I" before "don't" but not before "of" or "minute".
    /// </summary>
    public static class EnglishText
    {
        // Characters Tesseract reads a capital I as.
        private const string LooksLikeI = @"[1l|/!\[]";

        // Where a word can start: line start, a space, a dialogue dash, an opening quote or a tag's ">".
        private const string WordStart = @"(?<=^|[\s""“‘>\-—–])";

        private static readonly Regex IContraction = new Regex(WordStart + LooksLikeI + @"(['’])(m|ll|ve|d|M|LL|VE|D)\b", RegexOptions.Compiled);
        private static readonly Regex IContractionNoApostrophe = new Regex(WordStart + @"[\[|](m|ll|ve|d)\b", RegexOptions.Compiled);
        private static readonly Regex IShortWord = new Regex(WordStart + LooksLikeI + @"(?=(?:ts|t|f|n|s)(?:\b|['’]))", RegexOptions.Compiled);
        // One I-like character, or a pair with a bracket in it ("!]" in "and !] can't").
        private static readonly Regex IAlone = new Regex(WordStart + @"(" + LooksLikeI + @"|[1l|/!\[\]]?\][1l|/!]?)(?= +([a-z][\w'’]*))", RegexOptions.Compiled);
        // "Why should I! know" and "War Is part of nature".
        private static readonly Regex IBang = new Regex(@"(?<![\w'’])I!(?= +[a-z])", RegexOptions.Compiled);
        private static readonly Regex IsMidSentence = new Regex(@"(?<=[A-Za-z,] )Is\b", RegexOptions.Compiled);
        private static readonly Regex KnowCapital = new Regex(@"(?<=[a-z,’'] )K(?=(?:now|nows|nown|new)\b)", RegexOptions.Compiled);
        // Music notes (♪) come out as "*", "»", "J\"", "-»*", "~»" or "_".
        // A lone f, J, ¢ or ƒ starting a line is a note too ("f It'll soon be...", "- ¢ I'm in such...").
        private static readonly Regex MusicStart = new Regex(@"^(\s*(?:-\s+)?)(?:J""|[fJ¢ƒ]|[-~_]?[»*~_]+[»*]?)(?=\s)", RegexOptions.Compiled);
        private static readonly Regex MusicEnd = new Regex(@"(?<=\S)\s+(?:J""|[»*¢ƒ]+)\s*$", RegexOptions.Compiled);
        private static readonly Regex SlashBeforeCloser = new Regex(@"(?<=[A-Za-z.!?])(\s*)[/|]\s*\]", RegexOptions.Compiled);
        private static readonly Regex IAcronym = new Regex(@"(?<![\w.])1(?=\.[A-Z]\.)", RegexOptions.Compiled);

        // "1" stays a number before these.
        private static readonly HashSet<string> NumberFollowers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "of", "or", "and", "to", "more", "less", "out", "in", "by", "x", "for", "at", "a.m.", "p.m.", "am", "pm", "o'clock", "o’clock",
            "second", "seconds", "minute", "minutes", "hour", "hours", "day", "days", "week", "weeks", "month", "months",
            "year", "years", "time", "times", "mile", "miles", "meter", "meters", "foot", "feet", "inch", "inches",
            "percent", "point", "dollar", "dollars", "cent", "cents", "million", "billion", "thousand", "hundred", "left", "down", "up"
        };

        private static readonly Regex MixedToken = new Regex(@"(?<![\w'’])[A-Za-z'’]*[1|][A-Za-z'’1|]*(?![\w'’])", RegexOptions.Compiled);
        private static readonly Regex Word = new Regex(@"[A-Za-z]+(?:['’][A-Za-z]+)?", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> MissingApostrophe = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dont"] = "don't", ["didnt"] = "didn't", ["doesnt"] = "doesn't", ["isnt"] = "isn't", ["wasnt"] = "wasn't",
            ["werent"] = "weren't", ["arent"] = "aren't", ["couldnt"] = "couldn't", ["wouldnt"] = "wouldn't",
            ["shouldnt"] = "shouldn't", ["havent"] = "haven't", ["hasnt"] = "hasn't", ["hadnt"] = "hadn't",
            ["cant"] = "can't", ["Im"] = "I'm", ["Ive"] = "I've", ["youre"] = "you're", ["theyre"] = "they're",
            ["thats"] = "that's", ["whats"] = "what's", ["Dont"] = "Don't", ["Didnt"] = "Didn't", ["Cant"] = "Can't",
            ["Thats"] = "That's", ["Whats"] = "What's", ["Youre"] = "You're", ["Theyre"] = "They're", ["Isnt"] = "Isn't"
        };

        private static readonly Regex Spaces = new Regex(@"(?<=\S) {2,}(?=\S)", RegexOptions.Compiled);

        // Common words, so a misread word can be fixed even when the file never spells it right.
        private static readonly string[] CommonWords =
        {
            "the", "to", "it", "is", "in", "if", "i", "of", "that", "this", "the", "they", "them", "then", "there", "these",
            "what", "with", "will", "well", "all", "call", "tell", "still", "kill", "killed", "like", "little", "look", "let",
            "let's", "talk", "want", "just", "get", "got", "not", "but", "out", "at", "it's", "that's", "let", "light", "left",
            "life", "live", "love", "long", "last", "help", "hell", "fill", "fall", "feel", "feeling", "file", "film", "fault",
            "until", "till", "felt", "fell", "held", "hold", "told", "old", "cold", "child", "fault", "sit", "lift", "time",
            "think", "thing", "things", "take", "talk", "tall", "try", "trying", "try", "it'll", "i'll", "we'll", "you'll"
        };

        public static void Fix(List<OcrCleanup.Cue> cues)
        {
            var vocabulary = Vocabulary(cues);
            var apostrophe = cues.SelectMany(c => c.Lines).Sum(l => l.Count(ch => ch == '’')) >
                             cues.SelectMany(c => c.Lines).Sum(l => l.Count(ch => ch == '\'')) ? '’' : '\'';
            var squareBrackets = cues.SelectMany(c => c.Lines).Sum(l => l.Count(ch => ch == '[' || ch == ']')) >=
                                 cues.SelectMany(c => c.Lines).Sum(l => l.Count(ch => ch == '(' || ch == ')'));

            foreach (var cue in cues)
            {
                for (var i = 0; i < cue.Lines.Count; i++)
                {
                    var unclosedSquare = UnclosedBracket(cue.Lines, i, '[', ']');
                    var unopenedSquare = cue.Lines[i].IndexOf('[') < 0 && !OpenedEarlier(cue.Lines, i, '[', ']');
                    var line = FixI(KnowCapital.Replace(cue.Lines[i], "k"), apostrophe, unclosedSquare, unopenedSquare);
                    line = IBang.Replace(line, "I");
                    line = IsMidSentence.Replace(line, "is");
                    line = MusicStart.Replace(line, "$1♪");
                    line = MusicEnd.Replace(line, " ♪");
                    line = FixMixedWords(line, vocabulary);
                    line = FixMissingApostrophes(line, apostrophe);
                    line = Spaces.Replace(line, " ");
                    cue.Lines[i] = line;
                }
                FixBrackets(cue.Lines, squareBrackets);
            }
        }

        /// <summary>The I misreads on one line.</summary>
        /// <param name="unclosedSquare">The line has a "[" that no "]" closes, so it may be an I.</param>
        /// <param name="unopenedSquare">No "[" is open on the line, so a "]" in front of a word may be an I.</param>
        public static string FixI(string line, char apostrophe, bool unclosedSquare, bool unopenedSquare = true)
        {
            line = IContraction.Replace(line, m => IsBracketToKeep(m.Value[0], unclosedSquare) ? m.Value : "I" + m.Groups[1].Value + m.Groups[2].Value);
            line = IContractionNoApostrophe.Replace(line, m => IsBracketToKeep(m.Value[0], unclosedSquare) ? m.Value : "I" + apostrophe + m.Groups[1].Value);
            // "/t" is "It" at the start of a sentence and "it" inside one ("what her mission /s?").
            line = IShortWord.Replace(line, m =>
                IsBracketToKeep(m.Value[0], unclosedSquare) ? m.Value : Regex.IsMatch(line.Substring(0, m.Index), @"[a-z,]\s*$") ? "i" : "I");
            line = IAlone.Replace(line, m =>
            {
                var token = m.Groups[1].Value;
                if (token.Length == 1 && IsBracketToKeep(token[0], unclosedSquare)) return m.Value;
                if (token.IndexOf(']') >= 0 && !unopenedSquare) return m.Value;
                if (token == "1" && NumberFollowers.Contains(m.Groups[2].Value)) return m.Value;
                return "I";
            });
            line = IAcronym.Replace(line, "I");
            return line;
        }

        private static bool IsBracketToKeep(char c, bool unclosedSquare) => c == '[' && !unclosedSquare;

        /// <summary>Words with a "1" or "|" in them: picks the reading (l, I, t or i) that's a known word.</summary>
        public static string FixMixedWords(string line, IDictionary<string, int> vocabulary)
        {
            return MixedToken.Replace(line, m =>
            {
                var token = m.Value;
                if (!token.Any(char.IsLetter)) return token;

                var best = token;
                var bestCount = 0;
                foreach (var candidate in Candidates(token))
                {
                    vocabulary.TryGetValue(candidate.ToLowerInvariant(), out var count);
                    if (count == 0 && EnglishWords.Contains(candidate)) count = 1;
                    if (count > bestCount)
                    {
                        best = candidate;
                        bestCount = count;
                    }
                }
                if (bestCount > 0) return best;

                // Unknown word: a 1 between letters is nearly always an l.
                return Regex.Replace(token, @"(?<=[a-z])[1|](?=[a-z])", "l");
            });
        }

        private static IEnumerable<string> Candidates(string token)
        {
            var positions = Enumerable.Range(0, token.Length).Where(i => token[i] == '1' || token[i] == '|').Take(3).ToList();
            var options = new[] { 'l', 'I', 't', 'i' };
            var total = (int)Math.Pow(options.Length, positions.Count);
            for (var n = 0; n < total; n++)
            {
                var chars = token.ToCharArray();
                var k = n;
                foreach (var p in positions)
                {
                    var c = options[k % options.Length];
                    k /= options.Length;
                    // A capital I only starts a word; inside one it's an l or i.
                    if (c == 'I' && p > 0) c = 'l';
                    chars[p] = c;
                }
                yield return new string(chars);
            }
        }

        public static string FixMissingApostrophes(string line, char apostrophe) =>
            Word.Replace(line, m => MissingApostrophe.TryGetValue(m.Value, out var fixedWord) ? fixedWord.Replace('\'', apostrophe) : m.Value);

        /// <summary>
        /// "[ Door Opens /" → "[ Door Opens ]", "Door Opens ]" → "[ Door Opens ]", "(Sighs]" → "[Sighs]".
        /// A bracket opened on one line may close on a later line of the same cue.
        /// </summary>
        public static void FixBrackets(List<string> lines, bool squareBrackets)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                line = Regex.Replace(line, @"\(([^()\[\]]*)\]", "[$1]");
                line = Regex.Replace(line, @"\[([^()\[\]]*)\)", "[$1]");
                if (line.IndexOf('[') >= 0)
                {
                    var spaced = line.Contains("[ ");
                    line = SlashBeforeCloser.Replace(line, spaced ? " ]" : "]");
                }
                if (squareBrackets)
                {
                    // A ")" closing nothing in a file of [ ] descriptions is a misread "]".
                    if (line.IndexOf('(') < 0 && line.IndexOf('[') < 0 && Regex.IsMatch(line, @"^[^)]*[A-Za-z][^)]*\)\s*$") && !OpenedEarlier(lines, i, '(', ')'))
                        line = line.Substring(0, line.LastIndexOf(')')) + "]";
                }
                line = CloseUnclosed(lines, i, line, '[', ']');
                line = CloseUnclosed(lines, i, line, '(', ')');
                line = OpenUnopened(lines, i, line, '[', ']');
                line = OpenUnopened(lines, i, line, '(', ')');
                lines[i] = line;
            }
        }

        // "[ Typing /" → "[ Typing ]"; "[ Shouting" → "[ Shouting ]" when no later line closes it.
        private static string CloseUnclosed(List<string> lines, int index, string line, char open, char close)
        {
            var at = line.LastIndexOf(open);
            if (at < 0 || line.IndexOf(close, at) >= 0) return line;
            if (ClosedLater(lines, index, open, close)) return line;

            var inside = line.Substring(at + 1);
            // A letter-like closer only counts after a space: "[ Bell" ends in a real l.
            var misreadCloser = Regex.Match(inside, @"^(.*[A-Za-z.!?])(?:(\s*)[/|)}]|(\s+)[lIJ1])\s*$");
            if (misreadCloser.Success && misreadCloser.Groups[1].Value.Trim().Length > 0 && LooksLikeDescription(misreadCloser.Groups[1].Value))
                return line.Substring(0, at + 1) + misreadCloser.Groups[1].Value + misreadCloser.Groups[2].Value + misreadCloser.Groups[3].Value + close;

            // Only a description is closed here; "[ just want..." is an I, which FixI has already dealt with.
            if (!LooksLikeDescription(inside)) return line;
            // "[I'm not sure." is a stray mark in front of an I, not a description.
            if (Regex.IsMatch(inside, @"^\s*I(?:['’]|\s+[a-z])")) return line.Remove(at, inside.StartsWith(" ", StringComparison.Ordinal) ? 2 : 1);
            var spaced = inside.StartsWith(" ", StringComparison.Ordinal);
            return line.TrimEnd() + (spaced ? " " : "") + close;
        }

        // "Door Opens ]" → "[ Door Opens ]"; "|Door Opens]" → "[Door Opens]".
        private static string OpenUnopened(List<string> lines, int index, string line, char open, char close)
        {
            var at = line.IndexOf(close);
            if (at < 0 || line.LastIndexOf(open, at) >= 0) return line;
            if (OpenedEarlier(lines, index, open, close)) return line;

            var lead = Regex.Match(line, @"^(\s*(?:-\s*)?)");
            var start = lead.Length;
            var before = line.Substring(start, at - start);
            if (before.Trim().Length == 0) return line;

            // "That's what] saw." - in the middle of a sentence it's an I.
            if (close == ']' && Regex.IsMatch(before, @"[a-z,]\s*$") && Regex.IsMatch(line.Substring(at + 1), @"^\s+[a-z]"))
                return line.Substring(0, at).TrimEnd() + " I" + line.Substring(at + 1);

            var misread = Regex.Match(before, @"^([|({1l!/]|I(?=\s*[A-Z]))");
            if (misread.Success) return line.Substring(0, start) + open + line.Substring(start + 1);

            var spaced = at > 0 && line[at - 1] == ' ';
            return line.Substring(0, start) + open + (spaced ? " " : "") + line.Substring(start);
        }

        private static bool LooksLikeDescription(string inside)
        {
            var text = inside.TrimStart();
            return text.Length > 0 && (char.IsUpper(text[0]) || char.IsDigit(text[0]) || text[0] == '#');
        }

        private static bool ClosedLater(List<string> lines, int index, char open, char close)
        {
            for (var j = index + 1; j < lines.Count; j++)
            {
                var c = lines[j].IndexOf(close);
                var o = lines[j].IndexOf(open);
                if (c >= 0 && (o < 0 || c < o)) return true;
                if (o >= 0) return false;
            }
            return false;
        }

        private static bool OpenedEarlier(List<string> lines, int index, char open, char close)
        {
            for (var j = index - 1; j >= 0; j--)
            {
                var o = lines[j].LastIndexOf(open);
                var c = lines[j].LastIndexOf(close);
                if (o >= 0 && o > c) return true;
                if (c >= 0) return false;
            }
            return false;
        }

        /// <summary>True when line <paramref name="index"/> has a "[" that nothing closes, on it or a later line.</summary>
        public static bool UnclosedBracket(List<string> lines, int index, char open, char close)
        {
            var line = lines[index];
            var at = line.LastIndexOf(open);
            if (at < 0 || line.IndexOf(close, at) >= 0) return false;
            if (ClosedLater(lines, index, open, close)) return false;
            // A misread closer at the end ("[ Typing /") still makes it a description.
            return !Regex.IsMatch(line.Substring(at + 1), @"^\s*[A-Z#\d].*[/|lJ1\)}]\s*$");
        }

        private static Dictionary<string, int> Vocabulary(IEnumerable<OcrCleanup.Cue> cues)
        {
            var words = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var w in CommonWords) words[w] = 1;
            foreach (var line in cues.SelectMany(c => c.Lines))
            {
                foreach (Match m in Word.Matches(line))
                {
                    // Only whole words: "wi" from "wi1l" is not one.
                    var after = m.Index + m.Length;
                    if (m.Index > 0 && (line[m.Index - 1] == '1' || line[m.Index - 1] == '|')) continue;
                    if (after < line.Length && (line[after] == '1' || line[after] == '|')) continue;
                    var key = m.Value.ToLowerInvariant().Replace('’', '\'');
                    words.TryGetValue(key, out var n);
                    words[key] = n + 1;
                }
            }
            return words;
        }
    }
}
