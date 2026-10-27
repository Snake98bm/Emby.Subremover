using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Emby.Subremover.Core
{
    // Property names deliberately match ffprobe's JSON keys so any JSON serializer can bind them without attributes.
    public class ProbeResult
    {
        public List<ProbeStream> streams { get; set; } = new List<ProbeStream>();
        public ProbeFormat format { get; set; }

        public int Count(string codecType) =>
            streams.Count(s => string.Equals(s.codec_type, codecType, StringComparison.OrdinalIgnoreCase));

        public double? DurationSeconds =>
            double.TryParse(format?.duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;

        /// <summary>
        /// Matroska NUMBER_OF_FRAMES statistics are written by mkvmerge/MakeMKV. ffmpeg copies them unchanged when it
        /// rewrites or trims a file, so they can be stale in files last written by ffmpeg ("Lavf" encoder).
        /// </summary>
        public bool FrameCountsAreCurrent
        {
            get
            {
                string encoder = null;
                if (format?.tags != null)
                {
                    foreach (var kv in format.tags)
                    {
                        if (string.Equals(kv.Key, "encoder", StringComparison.OrdinalIgnoreCase)) encoder = kv.Value;
                    }
                }
                return encoder == null || encoder.IndexOf("Lavf", StringComparison.OrdinalIgnoreCase) < 0;
            }
        }
    }

    public class ProbeFormat
    {
        public string duration { get; set; }
        public string format_name { get; set; }
        public Dictionary<string, string> tags { get; set; }
    }

    public class ProbeStream
    {
        public int index { get; set; }
        public string codec_type { get; set; }
        public string codec_name { get; set; }
        public Dictionary<string, string> tags { get; set; }
        public Dictionary<string, int> disposition { get; set; }

        public bool IsSubtitle => string.Equals(codec_type, "subtitle", StringComparison.OrdinalIgnoreCase);

        public string Tag(string name)
        {
            if (tags == null) return null;
            foreach (var kv in tags)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        public bool Disposition(string name)
        {
            if (disposition == null) return false;
            foreach (var kv in disposition)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value != 0;
            }
            return false;
        }

        public string Language => Languages.Normalize(Tag("language"));

        public string Title => Tag("title") ?? string.Empty;

        public bool IsForced => Disposition("forced") || Title.IndexOf("forced", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool IsHearingImpaired =>
            Disposition("hearing_impaired")
            || Title.IndexOf("SDH", StringComparison.OrdinalIgnoreCase) >= 0
            || Title.IndexOf("hearing impaired", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool IsCommentary =>
            Disposition("comment") || Title.IndexOf("commentary", StringComparison.OrdinalIgnoreCase) >= 0;

        public string Describe() =>
            $"#{index} {Language} {codec_name}{(IsForced ? " forced" : "")}{(IsHearingImpaired ? " SDH" : "")}" +
            $"{(Disposition("comment") && Title.IndexOf("comment", StringComparison.OrdinalIgnoreCase) < 0 ? " commentary" : "")}" +
            $"{(Title.Length > 0 ? " \"" + Title + "\"" : "")}";
    }
}
