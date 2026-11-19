using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// The same file is in AudioRemover (Core/HashMatchTitle.cs), with its own namespace. Keep the two copies alike.
namespace Emby.Subremover.Core
{
    /// <summary>
    /// A "perfect" hash match isn't always for the right video: people upload subtitles under the wrong hash, so a
    /// provider can offer another film's, or the first film's for its sequel. This checks the subtitle's release name
    /// against the video's title and year before it is downloaded: fewer than half the title's words, a missing number
    /// from the title (a sequel's "2"), or another year turns it down. An episode is only turned down when its name says
    /// another season and episode; an empty name passes.
    /// </summary>
    public static class HashMatchTitle
    {
        // Words that say nothing about which film it is.
        private static readonly HashSet<string> Filler = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "in", "on", "at", "to", "for", "with", "from", "part"
        };

        private static readonly Dictionary<string, string> Roman = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10"
        };

        private static readonly Regex EpisodeCode = new Regex(@"\bs(\d{1,2})[ ._-]?e(\d{1,3})\b|\b(\d{1,2})x(\d{2,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Why a movie's hash match is for another title, or null when its name fits.</summary>
        public static string WhyNotMovie(string releaseName, string title, int? year)
        {
            if (string.IsNullOrWhiteSpace(releaseName) || string.IsNullOrWhiteSpace(title)) return null;
            var release = Words(releaseName);
            var wanted = Words(title).Where(w => !Filler.Contains(w)).Distinct().ToList();
            if (wanted.Count == 0) return null;

            // A sequel's number must be there: "Movie 2" isn't "Movie".
            var missingNumber = wanted.FirstOrDefault(w => IsNumber(w) && !release.Contains(w));
            if (missingNumber != null) return $"the name has no \"{missingNumber}\" from the title";

            var found = wanted.Count(release.Contains);
            if (found * 2 < wanted.Count) return "the name is for another title";

            if (year.HasValue)
            {
                var years = release.Where(w => w.Length == 4 && IsYear(w) && !wanted.Contains(w)).Select(int.Parse).ToList();
                if (years.Count > 0 && !years.Any(y => Math.Abs(y - year.Value) <= 1)) return $"the name says {years[0]}, the video is from {year}";
            }
            return null;
        }

        /// <summary>Why an episode's hash match is for another episode, or null when its name fits (or doesn't say).</summary>
        public static string WhyNotEpisode(string releaseName, int? season, int? episode)
        {
            if (string.IsNullOrWhiteSpace(releaseName) || !season.HasValue || !episode.HasValue) return null;
            var codes = EpisodeCode.Matches(releaseName).Cast<Match>()
                .Select(m => m.Groups[1].Success
                    ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
                    : (int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)))
                .ToList();
            if (codes.Count == 0 || codes.Contains((season.Value, episode.Value))) return null;
            return $"the name is for S{codes[0].Item1:00}E{codes[0].Item2:00}, the video is S{season:00}E{episode:00}";
        }

        // Lower-case words and numbers; roman numerals become numbers ("VII" = "7").
        private static HashSet<string> Words(string text)
        {
            var words = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0);
            return new HashSet<string>(words.Select(w => Roman.TryGetValue(w, out var n) ? n : w), StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsNumber(string word) => word.All(char.IsDigit);

        private static bool IsYear(string word) => IsNumber(word) && (word.StartsWith("19") || word.StartsWith("20"));
    }
}
