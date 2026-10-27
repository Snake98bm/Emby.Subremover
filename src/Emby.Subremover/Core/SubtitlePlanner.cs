using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Subremover.Core
{
    public class PlannerSettings
    {
        public ISet<string> KeepLanguages { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool KeepUndefined { get; set; } = true;
        public bool KeepForcedAnyLanguage { get; set; }
        public bool RemoveHearingImpaired { get; set; }
        public bool ExtractTextSubtitles { get; set; }
        public bool RemoveExtractedFromVideo { get; set; }

        public static ISet<string> ParseLanguages(string csv) =>
            new HashSet<string>(
                (csv ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Languages.Normalize),
                StringComparer.OrdinalIgnoreCase);
    }

    public class ExtractJob
    {
        public ProbeStream Stream { get; set; }
        public string OutputPath { get; set; }
        public bool ConvertToSrt { get; set; }
    }

    public class SubtitlePlan
    {
        public List<ProbeStream> Keep { get; } = new List<ProbeStream>();
        public List<ProbeStream> Remove { get; } = new List<ProbeStream>();
        public List<ExtractJob> Extract { get; } = new List<ExtractJob>();

        public bool NeedsRemux => Remove.Count > 0;
        public bool HasWork => Remove.Count > 0 || Extract.Count > 0;
    }

    public static class SubtitlePlanner
    {
        private static readonly HashSet<string> TextCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text", "microdvd", "subviewer", "subviewer1",
            "realtext", "sami", "stl", "jacosub", "mpl2", "pjs", "vplayer", "ttml"
        };

        private static readonly HashSet<string> AssCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ass", "ssa" };

        public static bool IsTextCodec(string codec) => codec != null && TextCodecs.Contains(codec);

        public static SubtitlePlan Plan(ProbeResult probe, PlannerSettings settings, string videoPath)
        {
            var plan = new SubtitlePlan();
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in probe.streams.Where(x => x.IsSubtitle))
            {
                if (!ShouldKeep(s, settings))
                {
                    plan.Remove.Add(s);
                    continue;
                }

                if (settings.ExtractTextSubtitles && IsTextCodec(s.codec_name))
                {
                    plan.Extract.Add(new ExtractJob
                    {
                        Stream = s,
                        OutputPath = SidecarPath(videoPath, s, usedPaths),
                        ConvertToSrt = !AssCodecs.Contains(s.codec_name)
                    });

                    if (settings.RemoveExtractedFromVideo)
                    {
                        plan.Remove.Add(s);
                        continue;
                    }
                }

                plan.Keep.Add(s);
            }

            return plan;
        }

        private static bool ShouldKeep(ProbeStream s, PlannerSettings settings)
        {
            if (settings.RemoveHearingImpaired && s.IsHearingImpaired) return false;
            if (settings.KeepLanguages.Contains(s.Language)) return true;
            if (s.Language == "und" && settings.KeepUndefined) return true;
            if (s.IsForced && settings.KeepForcedAnyLanguage) return true;
            return false;
        }

        // Emby picks up sidecars named "<video name>.<lang>[.sdh][.forced].<ext>".
        internal static string SidecarPath(string videoPath, ProbeStream s, ISet<string> usedPaths)
        {
            var dir = Path.GetDirectoryName(videoPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(videoPath);
            var ext = AssCodecs.Contains(s.codec_name ?? "") ? ".ass" : ".srt";
            var suffix = "." + s.Language + (s.IsHearingImpaired ? ".sdh" : "") + (s.IsForced ? ".forced" : "");

            var path = Path.Combine(dir, name + suffix + ext);
            if (!usedPaths.Add(path))
            {
                path = Path.Combine(dir, name + suffix + ".track" + s.index + ext);
                usedPaths.Add(path);
            }
            return path;
        }
    }
}
