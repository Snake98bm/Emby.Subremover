using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>The words people read in an .srt file, without cue numbers, timings or formatting tags.</summary>
    public static class SrtText
    {
        private static readonly Regex CueSplit = new Regex(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
        private static readonly Regex Timing = new Regex(@"^\s*\d+\s*$|^\s*\d+:\d\d:\d\d[,.]\d+\s*-->.*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Markup = new Regex(@"<[^>]*>|\{[^}]*\}", RegexOptions.Compiled);

        /// <summary>The text of each cue, in order; empty cues are left out.</summary>
        public static List<string> Cues(string srt) =>
            CueSplit.Split(srt ?? string.Empty)
                .Select(c => Markup.Replace(Timing.Replace(c, ""), "").Trim())
                .Where(c => c.Length > 0)
                .ToList();
    }
}
