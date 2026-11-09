using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Which Tesseract does the OCR: it is built into Emby's ffmpeg, so its version comes from that ffmpeg file
    /// (Tesseract stores "ocr-system' content='tesseract X.Y.Z" in it). Read once per ffmpeg file, for the settings help.
    /// </summary>
    public static class OcrEngineInfo
    {
        private static readonly object Gate = new object();
        private static string _cachedFor;
        private static string _cached;

        /// <returns>
        /// e.g. "Tesseract 5.2.0, built into Emby's ffmpeg 5.1-emby_2023_06_25_p6 (built with gcc 12.2.0)", or null when
        /// ffmpeg can't be found.
        /// </returns>
        public static string Describe(string ffmpegPath)
        {
            try
            {
                if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath)) return null;
                var key = ffmpegPath + "|" + File.GetLastWriteTimeUtc(ffmpegPath).Ticks;
                lock (Gate)
                {
                    if (_cachedFor == key) return _cached;
                }

                var tesseract = FindInFile(ffmpegPath, "content='tesseract ", '\'');
                var (version, compiler) = FfmpegVersion(ffmpegPath);
                var text = Format(tesseract, version, compiler);
                lock (Gate)
                {
                    _cachedFor = key;
                    _cached = text;
                }
                return text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string Format(string tesseract, string ffmpegVersion, string compiler)
        {
            var sb = new StringBuilder();
            sb.Append(tesseract != null ? "Tesseract " + tesseract : "Tesseract (version not found)");
            sb.Append(", built into Emby's ffmpeg");
            if (!string.IsNullOrEmpty(ffmpegVersion)) sb.Append(' ').Append(ffmpegVersion);
            if (!string.IsNullOrEmpty(compiler)) sb.Append(" (").Append(compiler).Append(')');
            return sb.ToString();
        }

        /// <summary>The text after <paramref name="marker"/> up to <paramref name="end"/>, searched through the whole file.</summary>
        internal static string FindInFile(string path, string marker, char end)
        {
            var needle = Encoding.ASCII.GetBytes(marker);
            var buffer = new byte[1 << 20];
            var carry = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read;
                while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
                {
                    var length = carry + read;
                    var at = IndexOf(buffer, length, needle);
                    if (at >= 0)
                    {
                        var start = at + needle.Length;
                        // Make sure the value is in the buffer too.
                        if (length - start < 64 && stream.Position < stream.Length)
                        {
                            Array.Copy(buffer, at, buffer, 0, length - at);
                            carry = length - at;
                            continue;
                        }
                        var sb = new StringBuilder();
                        for (var i = start; i < length && i < start + 64 && buffer[i] != (byte)end && buffer[i] >= 32 && buffer[i] < 127; i++)
                            sb.Append((char)buffer[i]);
                        return sb.Length > 0 ? sb.ToString() : null;
                    }
                    // Keep the tail in case the marker spans two reads.
                    carry = Math.Min(needle.Length - 1, length);
                    Array.Copy(buffer, length - carry, buffer, 0, carry);
                }
            }
            return null;
        }

        private static int IndexOf(byte[] haystack, int length, byte[] needle)
        {
            for (var i = 0; i <= length - needle.Length; i++)
            {
                if (haystack[i] != needle[0]) continue;
                var j = 1;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // "ffmpeg version 5.1-emby_2023_06_25_p6 Copyright ..." and "built with gcc 12.2.0 (Rev10, Built by MSYS2 project)".
        private static (string Version, string Compiler) FfmpegVersion(string ffmpegPath)
        {
            var psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } }
                return ParseVersion(output);
            }
        }

        public static (string Version, string Compiler) ParseVersion(string output)
        {
            string version = null, compiler = null;
            foreach (var raw in (output ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim();
                if (version == null && line.StartsWith("ffmpeg version ", StringComparison.Ordinal))
                    version = line.Substring("ffmpeg version ".Length).Split(' ')[0];
                else if (compiler == null && line.StartsWith("built with ", StringComparison.Ordinal))
                {
                    var c = line.Substring("built with ".Length);
                    var paren = c.IndexOf(" (", StringComparison.Ordinal);
                    compiler = "built with " + (paren > 0 ? c.Substring(0, paren) : c);
                }
            }
            return (version, compiler);
        }
    }
}
