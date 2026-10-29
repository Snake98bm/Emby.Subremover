using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>What one run did (or, in test mode, would do), added up over every video for the end of the run log.</summary>
    public class RunTotals
    {
        public int VideosRewritten { get; set; }
        public int TracksRemoved { get; set; }
        public int DefaultsChanged { get; set; }
        public int SubtitlesExtracted { get; set; }
        public int OcrFiles { get; set; }
        public int OcrFailed { get; set; }
        public int DefaultFiles { get; set; }
        public int LanguagesGuessed { get; set; }
        public int WrongTags { get; set; }
        public int SdhSpotted { get; set; }

        /// <summary>Test mode: counts what the plan would do. Extracted files that already exist aren't written again.</summary>
        public void AddPlan(SubtitlePlan plan)
        {
            if (plan.NeedsRemux)
            {
                VideosRewritten++;
                TracksRemoved += plan.Remove.Count;
                if (plan.NewDefault != null || plan.ClearDefault.Count > 0) DefaultsChanged++;
            }
            var extracted = plan.Extract.Where(e => !File.Exists(e.OutputPath)).Select(e => e.OutputPath).ToList();
            SubtitlesExtracted += extracted.Count;
            OcrFiles += plan.Ocr.Count;
            DefaultFiles += extracted.Concat(plan.Ocr.Select(o => o.OutputPath)).Count(IsDefaultFile);
        }

        public void AddExtracted(string path)
        {
            SubtitlesExtracted++;
            if (IsDefaultFile(path)) DefaultFiles++;
        }

        public void AddOcr(string path)
        {
            OcrFiles++;
            if (IsDefaultFile(path)) DefaultFiles++;
        }

        public void AddRemux(SubtitlePlan plan)
        {
            VideosRewritten++;
            TracksRemoved += plan.Remove.Count;
            if (plan.NewDefault != null || plan.ClearDefault.Count > 0) DefaultsChanged++;
        }

        private static bool IsDefaultFile(string path) =>
            Path.GetFileName(path ?? "").IndexOf(".default.", System.StringComparison.OrdinalIgnoreCase) >= 0;

        public string Describe(bool dryRun)
        {
            var lines = new List<string>
            {
                $"video files rewritten:      {VideosRewritten}",
                $"subtitle tracks removed:    {TracksRemoved}",
                $"default subtitle changed:   {DefaultsChanged}",
                $"subtitle files extracted:   {SubtitlesExtracted}",
                $"OCR subtitle files made:    {OcrFiles}" + (OcrFailed > 0 ? $" ({OcrFailed} failed)" : ""),
                $"files named .default:       {DefaultFiles}",
                $"subtitle languages guessed: {LanguagesGuessed}",
                $"wrong language tags found:  {WrongTags}",
                $"SDH spotted from the text:  {SdhSpotted}"
            };
            return (dryRun ? "Totals - what a real run would do:" : "Totals:") + "\n    " + string.Join("\n    ", lines);
        }
    }
}
