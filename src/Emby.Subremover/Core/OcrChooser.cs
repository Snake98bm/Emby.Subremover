using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Picks, for each subtitle, the better of two OCR readings of the same picture (fast and accurate model): the one with
    /// fewer words that look misread. A tie keeps the reading with more words (a dropped "I"), then the first one.
    /// </summary>
    public static class OcrChooser
    {
        /// <summary>Two cues this close in start time show the same picture.</summary>
        private const long SameCueMs = 100;

        private static readonly Regex Token = new Regex(@"\S+", RegexOptions.Compiled);
        private static readonly Regex Edges = new Regex(@"^[\s.,!?;:""“”‘(\[\]{}…♪—–-]+|[\s.,!?;:""“”’(\[\]{}…♪—–-]+$", RegexOptions.Compiled);
        private static readonly Regex LettersOnly = new Regex(@"^[A-Za-z]+(?:['’][A-Za-z]+)*['’]?$", RegexOptions.Compiled);

        public static List<OcrCleanup.Cue> Pick(List<OcrCleanup.Cue> first, List<OcrCleanup.Cue> second, bool english)
        {
            // Words that both readings contain somewhere are almost always right (2 = in both); for other languages
            // that's all there is to go on. A misread repeated in one reading only ("Il find", "Il saw") still counts 1.
            var inFirst = new HashSet<string>(first.SelectMany(c => c.Lines).SelectMany(Words).Select(w => w.ToLowerInvariant()));
            var inSecond = new HashSet<string>(second.SelectMany(c => c.Lines).SelectMany(Words).Select(w => w.ToLowerInvariant()));
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var w in inFirst.Concat(inSecond)) seen[w] = (inFirst.Contains(w) ? 1 : 0) + (inSecond.Contains(w) ? 1 : 0);

            var picked = new List<OcrCleanup.Cue>();
            string previousLine = null;
            var j = 0;
            foreach (var cue in first)
            {
                while (j < second.Count && second[j].StartMs < cue.StartMs - SameCueMs) j++;
                var other = j < second.Count && Math.Abs(second[j].StartMs - cue.StartMs) <= SameCueMs ? second[j] : null;
                if (other == null)
                {
                    picked.Add(cue);
                    if (cue.Lines.Count > 0) previousLine = cue.Lines[cue.Lines.Count - 1];
                    continue;
                }

                var chosen = new OcrCleanup.Cue { StartMs = cue.StartMs, EndMs = cue.EndMs };
                if (cue.Lines.Count == other.Lines.Count)
                {
                    // Same layout: each line on its own, so one model's good line doesn't bring in its bad one.
                    for (var k = 0; k < cue.Lines.Count; k++)
                    {
                        chosen.Lines.AddRange(Better(new[] { cue.Lines[k] }, new[] { other.Lines[k] }, english, seen, previousLine));
                        previousLine = chosen.Lines[chosen.Lines.Count - 1];
                    }
                }
                else
                {
                    chosen.Lines.AddRange(Better(cue.Lines, other.Lines, english, seen, previousLine));
                    if (chosen.Lines.Count > 0) previousLine = chosen.Lines[chosen.Lines.Count - 1];
                }
                picked.Add(chosen);
            }
            return picked;
        }

        private static IList<string> Better(IList<string> a, IList<string> b, bool english, IDictionary<string, int> seen, string previousLine)
        {
            var pa = Penalty(a, english, seen, previousLine);
            var pb = Penalty(b, english, seen, previousLine);
            return pb < pa || (pb == pa && WordCount(b) > WordCount(a)) ? b : a;
        }

        /// <summary>
        /// True when the text before ends a sentence (so the next line starts with a capital), false when it runs on
        /// ("the escape lifeboat" / "will commence"), null when it can't be told ("..." or nothing before).
        /// </summary>
        private static bool? ExpectCapital(string previousLine)
        {
            if (previousLine == null) return null;
            var text = OcrCleanup.StripTags(previousLine).TrimEnd();
            if (text.Length == 0) return null;
            if (text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith("…", StringComparison.Ordinal)) return null;
            var last = text[text.Length - 1];
            if (".?!♪\"”]):".IndexOf(last) >= 0) return true;
            if (char.IsLower(last) || last == ',') return false;
            return null;
        }

        /// <summary>How many words in the lines look misread.</summary>
        /// <param name="previousLine">The line shown just before, to tell whether the first word should be capitalized.</param>
        public static int Penalty(IEnumerable<string> lines, bool english, IDictionary<string, int> seen = null, string previousLine = null)
        {
            var penalty = 0;
            var before = previousLine;
            foreach (var line in lines)
            {
                var clean = OcrCleanup.StripTags(line);
                // A dialogue dash starts a new speaker, so a capital.
                var expect = Regex.IsMatch(clean, @"^\s*-") ? true : ExpectCapital(before);
                before = line;
                var position = 0;
                var previousToken = "";
                foreach (Match m in Token.Matches(clean))
                {
                    var raw = m.Value;
                    var token = Edges.Replace(raw, "");
                    var after = previousToken;
                    previousToken = raw;
                    if (token.Length == 0) continue;
                    position++;

                    if (english && char.IsLetter(token[0]) && token.Length > 1 && !token.StartsWith("I'", StringComparison.Ordinal) && !token.StartsWith("I’", StringComparison.Ordinal))
                    {
                        var lowerForm = char.ToLowerInvariant(token[0]) + token.Substring(1);
                        var capitalized = char.IsUpper(token[0]) && token.Skip(1).All(c => !char.IsUpper(c));
                        if (position == 1)
                        {
                            // "sorry?" after a full stop, "Will commence" running on from the line before.
                            if (expect == true && char.IsLower(token[0])) penalty += 1;
                            else if (expect == false && capitalized && EnglishWords.Contains(lowerForm)) penalty += 1;
                        }
                        else if (capitalized && Regex.IsMatch(after, @"[a-z,]$") && EnglishWords.Contains(lowerForm))
                        {
                            // "I like Opening shopping malls."
                            penalty += 1;
                        }
                    }

                    var hasLetter = token.Any(char.IsLetter);
                    var hasDigit = token.Any(char.IsDigit);
                    if (!hasLetter) continue; // numbers like 20 or 1-3-7

                    // "1t", "7510S" (but "36-hour", "E.R." and "T-minus" are fine).
                    if (hasDigit && !Regex.IsMatch(token, @"^\d+(?:-[A-Za-z]+|s|st|nd|rd|th)$")) { penalty += 2; continue; }
                    if (Regex.IsMatch(token, @"[|/\\{}~_*»«<>]")) { penalty += 2; continue; }
                    if (Regex.IsMatch(token, @"^(?:[A-Za-z]\.)+[A-Za-z]?\.?$")) continue; // I.Q., A.S.A.P.

                    foreach (var part in token.Split('-'))
                    {
                        if (part.Length == 0) continue;
                        if (!LettersOnly.IsMatch(part)) { penalty += 2; continue; }
                        if (OddCase(part)) { penalty += 2; continue; }
                        // A stray letter: "(s rumor control". Only a, I and O stand alone.
                        if (part.Length == 1)
                        {
                            if ("aAIO".IndexOf(part[0]) < 0) penalty += 1;
                            continue;
                        }
                        var known = english
                            ? EnglishWords.Contains(part)
                            : seen != null && seen.TryGetValue(part.ToLowerInvariant(), out var n) && n > 1;
                        if (!known && !(english && IsName(part, seen))) penalty += 1;
                    }
                }
            }
            return penalty;
        }

        // "COMMEeNCE", "lnThe": not lowercase, not Capitalized, not ALL CAPS (McDonald and O'Brien are fine).
        private static bool OddCase(string word)
        {
            var letters = word.Where(char.IsLetter).ToArray();
            if (letters.All(char.IsLower) || letters.All(char.IsUpper)) return false;
            if (char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower)) return false;
            return !Regex.IsMatch(word, @"^(?:Mc|Mac|O['’])[A-Z][a-z]+$");
        }

        // A capitalized word that isn't in the word list is a name when both readings have it ("Hopetully" is only in one).
        private static bool IsName(string word, IDictionary<string, int> seen) =>
            word.Length > 1 && char.IsUpper(word[0]) && seen != null && seen.TryGetValue(word.ToLowerInvariant(), out var n) && n > 1;

        private static int WordCount(IEnumerable<string> lines) => lines.Sum(l => Words(l).Count());

        private static IEnumerable<string> Words(string line) =>
            Token.Matches(OcrCleanup.StripTags(line)).Cast<Match>().Select(m => Edges.Replace(m.Value, "")).Where(w => w.Length > 0);
    }
}
