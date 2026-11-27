using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// A SubRemover version as releases and builds write it: "1.0.7", "v1.0.7" (a release tag), "1.0.7.0" (an assembly
    /// version) or "1.0.7-alpha.3" (a local build). A version with a suffix comes before the same version without one.
    /// </summary>
    internal sealed class PluginVersion : IComparable<PluginVersion>
    {
        private static readonly Regex Pattern = new Regex(@"^\s*[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+.*)?\s*$");

        private PluginVersion(int[] parts, string suffix)
        {
            Parts = parts;
            Suffix = suffix;
        }

        /// <summary>Major, minor, build and revision; missing ones are 0.</summary>
        internal int[] Parts { get; }

        /// <summary>"alpha.3", or null for a release.</summary>
        internal string Suffix { get; }

        internal static PluginVersion Parse(string text) =>
            TryParse(text, out var v) ? v : throw new FormatException("Not a version: '" + text + "'");

        internal static bool TryParse(string text, out PluginVersion version)
        {
            version = null;
            var m = Pattern.Match(text ?? "");
            if (!m.Success) return false;
            var parts = new int[4];
            for (var i = 0; i < 4; i++)
            {
                var g = m.Groups[i + 1];
                if (g.Success && !int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return false;
            }
            version = new PluginVersion(parts, m.Groups[5].Success ? m.Groups[5].Value : null);
            return true;
        }

        internal static PluginVersion From(Version v) =>
            new PluginVersion(new[] { Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0) }, null);

        /// <summary>Same numbers, ignoring the suffix: a downloaded DLL's assembly version against the release tag.</summary>
        internal bool SameNumbers(PluginVersion other) => other != null && CompareNumbers(this, other) == 0;

        public int CompareTo(PluginVersion other)
        {
            if (other == null) return 1;
            var byNumbers = CompareNumbers(this, other);
            if (byNumbers != 0) return byNumbers;
            if (Suffix == null) return other.Suffix == null ? 0 : 1;
            if (other.Suffix == null) return -1;
            return CompareSuffix(Suffix, other.Suffix);
        }

        internal bool IsNewerThan(PluginVersion other) => CompareTo(other) > 0;

        private static int CompareNumbers(PluginVersion a, PluginVersion b)
        {
            for (var i = 0; i < 4; i++)
            {
                var c = a.Parts[i].CompareTo(b.Parts[i]);
                if (c != 0) return c;
            }
            return 0;
        }

        // "alpha.10" after "alpha.9": dot-separated, numbers compared as numbers.
        private static int CompareSuffix(string a, string b)
        {
            var x = a.Split('.');
            var y = b.Split('.');
            for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                var xn = int.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xi);
                var yn = int.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yi);
                var c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
                if (c != 0) return c;
            }
            return x.Length.CompareTo(y.Length);
        }

        /// <summary>"1.0.7", "1.0.7.1" when there's a revision, with "-alpha.3" when there's a suffix.</summary>
        public override string ToString() =>
            string.Join(".", Parts[3] != 0 ? Parts : new[] { Parts[0], Parts[1], Parts[2] }) + (Suffix == null ? "" : "-" + Suffix);
    }
}
