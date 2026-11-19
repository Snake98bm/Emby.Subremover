using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Why a backup was made, read from the notes it carries (<see cref="BackupNotes"/> for videos, the "Subtitle file: ..."
    /// notes for .srt files), for the restore list's "Why" filter. One backup can have several reasons.
    /// </summary>
    public static class BackupReasons
    {
        public const string Any = "any";

        /// <summary>Each reason: its value, the label shown, and the test on the backup's notes.</summary>
        public static readonly IReadOnlyList<(string Value, string Label, Func<string, bool> Matches)> All = new List<(string, string, Func<string, bool>)>
        {
            ("tracks", "Video: subtitle tracks removed", n => n.StartsWith("Removed ", StringComparison.Ordinal) && n.Contains(" subtitle")),
            ("extracted", "Video: subtitles extracted to files", n => Has(n, "extracted ")),
            ("ocr", "Video: OCR subtitle made", n => Has(n, "(OCR")),
            ("default", "Video: default subtitle changed", n => !IsSubtitleNote(n) && (Has(n, "default") || n == BackupNotes.OldDefaultNote)),
            ("language", "Video: language tags written", n => Has(n, "language written into")),
            ("ads", "Subtitle file: ads and credits removed", n => IsSubtitleNote(n) && Has(n, " ad line")),
            ("timing", "Subtitle file: timing fixed (synced to the speech)", n => IsSubtitleNote(n) && Has(n, "moved every cue")),
            ("deleted", "Subtitle file: deleted (out of sync, or a worse copy)", n => IsSubtitleNote(n) && Has(n, "deleted")),
        };

        public const string UnrecordedValue = "unrecorded";
        public const string UnrecordedLabel = "Not recorded (backups made before 0.7.7)";

        /// <summary>The reasons this backup was made.</summary>
        public static List<string> Of(BackupEntry backup)
        {
            if (backup.Changes == null) return new List<string> { UnrecordedValue };
            return All.Where(r => backup.Changes.Any(n => n != null && r.Matches(n))).Select(r => r.Value).ToList();
        }

        /// <summary>The backups with this reason; "any", empty or an unknown value keeps them all.</summary>
        public static IEnumerable<BackupEntry> Filter(IEnumerable<BackupEntry> backups, string why)
        {
            if (string.IsNullOrEmpty(why) || string.Equals(why, Any, StringComparison.OrdinalIgnoreCase)) return backups;
            if (!All.Any(r => r.Value == why) && why != UnrecordedValue) return backups;
            return backups.Where(b => Of(b).Contains(why));
        }

        /// <summary>Each reason with how many of these backups have it; reasons none of them have are left out.</summary>
        public static List<(string Value, string Label, int Count)> Counts(IEnumerable<BackupEntry> backups)
        {
            var reasons = backups.Select(Of).ToList();
            return All.Select(r => (r.Value, r.Label, reasons.Count(x => x.Contains(r.Value))))
                .Concat(new[] { (UnrecordedValue, UnrecordedLabel, reasons.Count(x => x.Contains(UnrecordedValue))) })
                .Where(r => r.Item3 > 0)
                .ToList();
        }

        private static bool IsSubtitleNote(string note) => note.StartsWith("Subtitle file:", StringComparison.Ordinal);

        private static bool Has(string note, string what) => note.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
