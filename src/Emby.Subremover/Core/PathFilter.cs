using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// "Only process paths containing": one or more texts separated by ';'. A path matches when it contains any of them;
    /// capitals don't matter. Commas can't separate texts because titles contain them ("Rain, Wind And Coffee").
    /// </summary>
    public static class PathFilter
    {
        public static List<string> Parts(string filter) =>
            (filter ?? string.Empty).Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        public static bool IsEmpty(string filter) => Parts(filter).Count == 0;

        /// <summary>True when the filter is empty or the path contains one of its texts.</summary>
        public static bool Matches(string path, string filter)
        {
            var parts = Parts(filter);
            return parts.Count == 0 || parts.Any(p => (path ?? string.Empty).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
