using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    public class LanguageGuess
    {
        /// <summary>The guessed language code, or null when the text doesn't say clearly enough.</summary>
        public string Language { get; set; }

        /// <summary>Why, for the log: e.g. "184 of 640 words are common French words".</summary>
        public string Reason { get; set; }

        /// <summary>Set when the text looks like SDH, with why.</summary>
        public string SdhReason { get; set; }

        /// <summary>Common-word hits per language, when the text was judged by its words (null when the writing system decided).</summary>
        public IDictionary<string, int> Hits { get; set; }

        /// <summary>How many words the hits are out of.</summary>
        public int Words { get; set; }
    }

    /// <summary>
    /// Guesses the language of subtitle text. Writing systems used by one language (Greek, Hebrew, Thai, Korean, Japanese,
    /// Chinese) decide by themselves; otherwise the language whose most common words make up the largest share of the text
    /// wins, as long as it's clearly ahead of the next one. Close neighbours have their own lists (Catalan next to Spanish,
    /// Macedonian next to Bulgarian, Marathi next to Hindi...), so their text isn't simply given the nearest big language.
    /// </summary>
    public static class LanguageGuesser
    {
        public const int MinimumWords = 30;

        // Languages told apart by their writing system rather than by words.
        private static readonly HashSet<string> ScriptLanguages = new HashSet<string> { "gre", "heb", "tha", "kor", "jpn", "chi" };

        // The commonest short words of everyday speech. Words that look the same in close languages (Danish/Norwegian,
        // Croatian/Serbian, Czech/Slovak) are in both lists; the words that differ decide between them.
        private static readonly Dictionary<string, HashSet<string>> CommonWords = new Dictionary<string, HashSet<string>>
        {
            ["eng"] = Words("the and you that is it to of what this have was for with are not he we they me my your be just know can all there here right okay yeah get going want don think"),
            ["fre"] = Words("le la les de des et est que qui je tu il elle nous vous ils pas une un du en ce ça mais pour avec sur dans oui non bien tout moi toi suis êtes fait"),
            ["ger"] = Words("der die das und ist ich du nicht sie es wir ihr ein eine zu mit auf den dem ja nein was wie auch noch hier bin hast habe kann mir mich dich"),
            ["spa"] = Words("el la los las de que y en un una es no por con para lo se su qué está estoy pero muy bien sí yo tú él ella eso esto aquí vamos"),
            ["ita"] = Words("il lo la gli le di che e è un una non per con mi ti si sono sei ma come cosa questo quello qui bene ho hai della anche"),
            ["por"] = Words("o a os as de que e em um uma não é com para por se eu você ele ela isso isto aqui está estou mas muito bem sim do da"),
            ["dut"] = Words("de het een en is ik je jij niet dat wat van op te met zijn hij zij we wij ze maar ook hier er nee ja goed heb hebt"),
            ["swe"] = Words("och det att jag du är inte en ett som på för med har vi han hon de vad den här till kan ska nej ja mig dig"),
            ["nor"] = Words("og det at jeg du er ikke en et som på for med har vi han hun de hva den her til kan skal nei ja meg deg"),
            ["dan"] = Words("og det at jeg du er ikke en et som på for med har vi han hun de hvad den her til kan skal nej ja mig dig"),
            ["fin"] = Words("ja on ei se että mitä minä sinä hän me te he tämä tuo oli olen olet kun mutta niin kyllä nyt vain jo siitä sen"),
            ["pol"] = Words("nie się to jest że w na i z do co jak ale tak ja ty on ona mi mnie cię jestem czy już tu go"),
            ["cze"] = Words("je to se na že a v do co jak ale tak já ty on ona mi mě tě jsem jsi není už tady proč"),
            ["slo"] = Words("je to sa na že a v do čo ako ale tak ja ty on ona mi ma som si nie už tu prečo"),
            ["hun"] = Words("a az és hogy nem van egy is de ez meg mi te én ő vagy volt csak már itt mit jól igen"),
            ["rum"] = Words("și de la nu în o un că ce se pe cu sunt ești este eu tu el ea mai dar aici bine da"),
            ["tur"] = Words("bir ve bu ne de da ben sen o için çok var mı mi değil ama evet hayır şey gibi daha"),
            ["hrv"] = Words("je i u na da se ne to su sam si što kako ali ja ti on ona mi me te ovo ovdje dobro"),
            ["srp"] = Words("je i u na da se ne to su sam si šta kako ali ja ti on ona mi me te ovo ovde dobro " +
                            "је и у на да се не то су сам си шта како али ја ти он она ми ме те ово овде добро"),
            ["mac"] = Words("и во не на јас дека тој со ова како но вие ние ти да така е сум ме што"),
            ["slv"] = Words("je in v na da se ne to so sem si kaj kako ampak jaz ti on ona mi me te tukaj dobro"),
            ["ind"] = Words("yang dan di ini itu tidak aku kau kamu dia kita kami apa ada akan dengan untuk saya ya tapi bisa karena saja nggak"),
            ["may"] = Words("yang dan di ini itu tidak aku kau kamu dia kita kami apa ada akan dengan untuk saya ya tapi boleh kerana sahaja awak"),
            ["cat"] = Words("el la els les de que i en un una és no per amb em et hi ho però molt aquí què això jo tu"),
            ["glg"] = Words("o a os as de que e en un unha non é con para por se eu ti el ela iso isto aquí está estou pero moi ben si"),
            ["vie"] = Words("không là có của và tôi anh em này được một những cho đi với người đã rồi"),
            ["rus"] = Words("и в не на я что он с это как но вы мы ты да нет так его все она меня"),
            ["ukr"] = Words("і в не на я що він з це як але ви ми ти так його все вона є мене"),
            ["bul"] = Words("и в не на аз че той с това как но вие ние ти да така е съм ме"),
            ["ara"] = Words("في من على أن لا ما هذا هل أنا إلى كان هو هي نحن أنت"),
            ["per"] = Words("این از به که را با است نه من تو او ما شما چه آن"),
            ["urd"] = Words("ہے میں کی کے کا اور یہ نہیں کو سے آپ ہم تم کیا وہ"),
            ["hin"] = Words("है में की के का और यह नहीं को से आप हम तुम क्या वह मैं हैं कि"),
            ["mar"] = Words("आहे मी तू तुम्ही आम्ही नाही आणि काय हे ते ला मला तो ती"),
            ["nep"] = Words("छ म तिमी हामी के यो त्यो र छैन हो गर्न मलाई तपाईं"),
        };

        private static HashSet<string> Words(string list) =>
            new HashSet<string>(list.Split(' '), StringComparer.Ordinal);

        // Apostrophes split words: "c'est" gives "est", "don't" gives "don". Combining marks stay in the word, or
        // Hindi words would break apart at their vowel signs.
        private static readonly Regex WordPattern = new Regex(@"[\p{L}\p{M}]+", RegexOptions.Compiled);

        // Japanese needs more than a stray kana (e.g. a long-vowel mark in a name) among Chinese characters.
        public const double MinimumKanaShare = 0.05;

        public static LanguageGuess Guess(string subtitleText)
        {
            var text = string.Join("\n", SrtText.Cues(subtitleText));

            var script = ScriptOnlyLanguage(text);
            if (script != null) return script;

            var words = WordPattern.Matches(text.ToLower(CultureInfo.InvariantCulture)).Cast<Match>()
                .Select(m => m.Value)
                .ToList();
            if (words.Count < MinimumWords)
                return new LanguageGuess { Reason = $"only {words.Count} words of text" };

            var scores = CommonWords
                .Select(kv => new { Language = kv.Key, Hits = words.Count(kv.Value.Contains) })
                .OrderByDescending(s => s.Hits)
                .ToList();
            var hits = scores.ToDictionary(s => s.Language, s => s.Hits);
            var best = scores[0];
            var second = scores[1];
            var name = Languages.NameOf(best.Language);

            if (best.Hits < words.Count * 0.15)
                return new LanguageGuess { Hits = hits, Words = words.Count, Reason = $"no language's common words stand out (best: {name}, {best.Hits} of {words.Count} words)" };
            if (best.Hits - second.Hits < Math.Max(3, best.Hits / 10))
                return new LanguageGuess { Hits = hits, Words = words.Count, Reason = $"{name} and {Languages.NameOf(second.Language)} are too close ({best.Hits} and {second.Hits} of {words.Count} words)" };

            return new LanguageGuess { Language = best.Language, Hits = hits, Words = words.Count, Reason = $"{best.Hits} of {words.Count} words are common {name} words" };
        }

        /// <summary>Below this share of the tagged language's common words, the text isn't that language (real text has 25%+).</summary>
        public const double RuledOutShare = 0.05;

        /// <summary>
        /// True when the text clearly isn't the tagged language even though it can't be named: e.g. a track tagged English
        /// whose text has almost none of the common English words. Only for tags with a word list.
        /// </summary>
        public static bool RulesOutTag(LanguageGuess guess, string tag) =>
            guess?.Hits != null && guess.Words >= MinimumWords
            && guess.Hits.TryGetValue(tag, out var tagHits) && tagHits < guess.Words * RuledOutShare;

        /// <summary>
        /// True only when the text clearly isn't in the tagged language: written in another writing system, or with the
        /// tagged language's common words far behind. A close neighbour (e.g. Bosnian text read as Croatian, or a tag with
        /// no word list to check) keeps its tag.
        /// </summary>
        public static bool ContradictsTag(LanguageGuess guess, string tag)
        {
            if (guess?.Language == null || string.Equals(guess.Language, tag, StringComparison.OrdinalIgnoreCase)) return false;
            if (guess.Hits == null) return CommonWords.ContainsKey(tag);
            if (ScriptLanguages.Contains(tag)) return true;
            return guess.Hits.TryGetValue(tag, out var tagHits) && tagHits < guess.Hits[guess.Language] / 2;
        }

        // Writing systems that belong to one language. Cyrillic, Arabic and Devanagari are shared, so they go to the word lists.
        private static LanguageGuess ScriptOnlyLanguage(string text)
        {
            int letters = 0, greek = 0, hebrew = 0, thai = 0, hangul = 0, kana = 0, han = 0;
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (c >= '\u0370' && c <= '\u03FF') greek++;
                else if (c >= '\u0590' && c <= '\u05FF') hebrew++;
                else if (c >= '\u0E00' && c <= '\u0E7F') thai++;
                else if (c >= '\uAC00' && c <= '\uD7AF') hangul++;
                else if (c >= '\u3040' && c <= '\u30FF') kana++;
                else if (c >= '\u4E00' && c <= '\u9FFF') han++;
            }
            if (letters < MinimumWords) return null;

            string language = null;
            var count = 0;
            void Consider(string code, int n)
            {
                if (n > count) { language = code; count = n; }
            }
            Consider("gre", greek);
            Consider("heb", hebrew);
            Consider("tha", thai);
            Consider("kor", hangul);
            // Japanese mixes kana with Chinese characters; Chinese has (almost) none.
            Consider(kana >= (kana + han) * MinimumKanaShare && kana > 0 ? "jpn" : "chi", kana + han);

            if (language == null || count < letters / 2) return null;
            return new LanguageGuess { Language = language, Reason = $"written in {Languages.NameOf(language)} script ({count} of {letters} letters)" };
        }
    }
}
