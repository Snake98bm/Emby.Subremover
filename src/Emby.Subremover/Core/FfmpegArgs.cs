using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Emby.Subremover.Core
{
    public static class FfmpegArgs
    {
        private static readonly Dictionary<string, string> MuxerByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".mkv", "matroska" },
            { ".mk3d", "matroska" },
            { ".webm", "webm" },
            { ".mp4", "mp4" },
            { ".m4v", "mp4" },
            { ".mov", "mov" },
            { ".avi", "avi" },
            { ".ts", "mpegts" },
            { ".m2ts", "mpegts" },
            { ".mts", "mpegts" },
        };

        public static string MuxerFor(string path) =>
            MuxerByExtension.TryGetValue(Path.GetExtension(path) ?? "", out var muxer) ? muxer : null;

        public static IList<string> Probe(string input) => new List<string>
        {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", input
        };

        public static IList<string> Remux(string input, string output, string muxer, IEnumerable<int> removeStreamIndexes)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-map", "0" };
            foreach (var index in removeStreamIndexes.OrderBy(i => i))
            {
                args.Add("-map");
                args.Add("-0:" + index);
            }
            args.AddRange(new[]
            {
                "-c", "copy", "-map_metadata", "0", "-map_chapters", "0",
                "-max_muxing_queue_size", "4096", "-f", muxer, output
            });
            return args;
        }

        // The format is given explicitly because the output is written to a temporary name first.
        public static IList<string> Extract(string input, int streamIndex, string output, bool convertToSrt) => new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input,
            "-map", "0:" + streamIndex, "-c:s", convertToSrt ? "srt" : "copy", "-f", convertToSrt ? "srt" : "ass", output
        };

        // graphicsub2text exists only in Emby's ffmpeg build. The tessdata path is relative to the process working
        // directory because a Windows drive colon would need double escaping inside a filter graph.
        public static IList<string> Ocr(string input, int streamIndex, string tesseractLanguage, string output) => new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input,
            "-filter_complex",
            $"[0:{streamIndex}]graphicsub2text=tessdata_path=tessdata/:language={tesseractLanguage}:ocr_mode=lstm:recognize=italic[ocr]",
            "-map", "[ocr]", "-c:s", "srt", "-f", "srt", output
        };

        // .NET Standard 2.0 has no ProcessStartInfo.ArgumentList, so quote per the Windows/.NET argv rules.
        public static string ToCommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    sb.Append('\\', backslashes);
                }
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
