using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Tells SDH subtitles from regular ones by their text: SDH describes sounds ("[door slams]", "(sighs)") and names
    /// speakers ("JOHN: Hello"). Regular subtitles do that only now and then.
    /// </summary>
    public static class SdhDetector
    {
        public const int MinimumCues = 20;
        public const double MinimumShare = 0.10;

        private static readonly Regex SoundOrSpeaker = new Regex(@"\[[^\]]+\]|\([^)]+\)|^[-\s]*[A-Z][A-Z .'-]{1,20}:", RegexOptions.Multiline | RegexOptions.Compiled);

        public static bool Looks(string subtitleText, out string reason)
        {
            var cues = SrtText.Cues(subtitleText);
            var marked = cues.Count(c => SoundOrSpeaker.IsMatch(c));
            reason = $"{marked} of {cues.Count} lines describe sounds or name the speaker";
            return cues.Count >= MinimumCues && marked >= cues.Count * MinimumShare;
        }
    }
}
