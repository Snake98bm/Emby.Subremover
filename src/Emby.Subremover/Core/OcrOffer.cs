using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Emby.Subremover.Core
{
    /// <summary>A picture subtitle track that can be read with OCR for Emby's subtitle search.</summary>
    public class OcrOffer
    {
        public string VideoPath { get; set; }
        public int StreamIndex { get; set; }
        public string Language { get; set; }
        public string TesseractLanguage { get; set; }
        public bool IsForced { get; set; }
        public bool IsHearingImpaired { get; set; }
        public string Description { get; set; }

        /// <summary>The picture tracks in a video that can be OCR'd into the searched language.</summary>
        public static List<OcrOffer> For(ProbeResult probe, string videoPath, string searchedLanguage)
        {
            var language = Languages.Normalize(searchedLanguage);
            var tesseract = Languages.TesseractCode(language);
            if (tesseract == null) return new List<OcrOffer>();

            // Tracks without a language are offered too: most of them are in the video's main language.
            return probe.streams
                .Where(s => s.IsSubtitle && SubtitlePlanner.IsPictureCodec(s.codec_name) && !s.IsCommentary && (s.Language == language || s.Language == "und"))
                .Select(s => new OcrOffer
                {
                    VideoPath = videoPath,
                    StreamIndex = s.index,
                    Language = language,
                    TesseractLanguage = tesseract,
                    IsForced = s.IsForced,
                    IsHearingImpaired = s.IsHearingImpaired,
                    Description = $"OCR of the built-in subtitles {s.Describe()}"
                })
                .ToList();
        }

        // The id Emby hands back when the offer is picked. The path goes last because it may contain '|'.
        public string ToId() =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("|", "1", StreamIndex, Language, TesseractLanguage, IsForced, IsHearingImpaired, VideoPath)))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static OcrOffer FromId(string id)
        {
            var base64 = id.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split(new[] { '|' }, 7);
            if (parts.Length != 7 || parts[0] != "1") throw new ArgumentException("Not a SubRemover OCR id: " + id);
            return new OcrOffer
            {
                StreamIndex = int.Parse(parts[1]),
                Language = parts[2],
                TesseractLanguage = parts[3],
                IsForced = bool.Parse(parts[4]),
                IsHearingImpaired = bool.Parse(parts[5]),
                VideoPath = parts[6]
            };
        }
    }
}
