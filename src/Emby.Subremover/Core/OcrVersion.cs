using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Which OCR made a subtitle file, so files from an older OCR can be redone ("Redo if made by an older OCR").
    /// </summary>
    public static class OcrVersion
    {
        /// <summary>
        /// Raise this only when OCR output gets clearly better, so older files are worth redoing.
        /// 1 = 0.7.6: end times for Blu-ray subtitles, English cleanup, fast/accurate/both models.
        /// </summary>
        public const int Current = 1;

        /// <summary>Both models read better than either one alone; Fast and Accurate count the same.</summary>
        public static int Rank(OcrModels model) => model == OcrModels.Both ? 1 : 0;

        public static OcrRecord Record(OcrModels model) =>
            new OcrRecord { Version = Current, Model = model.ToString(), MadeUtc = DateTime.UtcNow };

        /// <summary>True when the file was made by today's OCR with a model at least as good as the one chosen now.</summary>
        public static bool IsCurrent(OcrRecord record, OcrModels chosen)
        {
            if (record == null || record.Version < Current) return false;
            return !Enum.TryParse(record.Model, out OcrModels made) || Rank(made) >= Rank(chosen);
        }

        /// <summary>
        /// A fingerprint of the words in an .srt, ignoring numbering, timing, tags and line endings, so the file Emby saved
        /// from the subtitle search can be found again next to the video.
        /// </summary>
        public static string TextHash(string srt)
        {
            var text = string.Join("\n", SrtText.Cues(srt).Select(c => c.Replace("\r", "")));
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>Which subtitle-search OCR files next to a video were made by an older OCR, and how to redo them.</summary>
    public static class SearchOcrRedo
    {
        public static List<OcrJob> Jobs(string videoPath, ProbeResult probe, IEnumerable<SearchOcrRecord> records, OcrModels chosen,
            Func<string, string> readText = null)
        {
            readText = readText ?? File.ReadAllText;
            var jobs = new List<OcrJob>();
            foreach (var record in records.Where(r => string.Equals(r.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase)))
            {
                if (OcrVersion.IsCurrent(record, chosen)) continue;

                var file = FindFile(videoPath, record, readText);
                if (file == null) continue; // deleted in Emby, or edited since: leave it alone

                var stream = Track(probe, record);
                if (stream == null) continue;
                jobs.Add(new OcrJob
                {
                    Stream = stream,
                    OutputPath = file,
                    TesseractLanguage = Languages.TesseractCode(stream.Language),
                    SearchRecord = record
                });
            }
            return jobs;
        }

        /// <summary>The file Emby saved: the recorded one if it's still there, else the .srt next to the video with the same text.</summary>
        internal static string FindFile(string videoPath, SearchOcrRecord record, Func<string, string> readText)
        {
            if (record.FilePath != null && File.Exists(record.FilePath) && Same(record.FilePath, record, readText)) return record.FilePath;
            var folder = Path.GetDirectoryName(videoPath);
            var stem = Path.GetFileNameWithoutExtension(videoPath) + ".";
            if (folder == null || !Directory.Exists(folder)) return null;
            return Directory.GetFiles(folder, "*.srt")
                .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(f => Same(f, record, readText));
        }

        private static bool Same(string file, SearchOcrRecord record, Func<string, string> readText)
        {
            try { return OcrVersion.TextHash(readText(file)) == record.TextHash; }
            catch (Exception) { return false; }
        }

        /// <summary>The recorded picture track: same codec, language and title; its old index decides between look-alikes.</summary>
        internal static ProbeStream Track(ProbeResult probe, SearchOcrRecord record)
        {
            var matches = probe?.streams?.Where(s => s.IsSubtitle && record.IsTrack(s)).ToList() ?? new List<ProbeStream>();
            if (matches.Count == 1) return matches[0];
            return matches.FirstOrDefault(s => s.index == record.StreamIndex);
        }
    }

    public class OcrRecord
    {
        public int Version { get; set; }
        public string Model { get; set; }
        public DateTime MadeUtc { get; set; }
    }

    /// <summary>OCR handed to Emby from its subtitle search. Emby saves the file itself, under a name we don't choose.</summary>
    public class SearchOcrRecord : OcrRecord
    {
        public string VideoPath { get; set; }

        /// <summary>The picture track, to find it again even if a run has since removed other tracks.</summary>
        public int StreamIndex { get; set; }
        public string Codec { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }

        /// <summary>Fingerprint of the text given to Emby (<see cref="OcrVersion.TextHash"/>).</summary>
        public string TextHash { get; set; }

        /// <summary>The file, once known: set when the text was saved by SubRemover itself or found next to the video.</summary>
        public string FilePath { get; set; }

        public bool IsTrack(ProbeStream s) =>
            string.Equals(s.codec_name, Codec, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Language ?? "", Language ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Tag("title") ?? "", Title ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// The subtitle-search OCR records, in their own file: the search runs while a SubRemover run may be holding the
    /// main state, and a run's save would otherwise wipe what the search wrote.
    /// </summary>
    public class SearchOcrStore
    {
        private static readonly object Lock = new object();
        private readonly string _path;
        private readonly IJsonSerializer _json;

        public SearchOcrStore(string folder, IJsonSerializer json)
        {
            _path = Path.Combine(folder, "Emby.Subremover.search-ocr.json");
            _json = json;
        }

        public List<SearchOcrRecord> Load()
        {
            lock (Lock)
            {
                try
                {
                    return File.Exists(_path) ? (_json.DeserializeFromFile<List<SearchOcrRecord>>(_path) ?? new List<SearchOcrRecord>()) : new List<SearchOcrRecord>();
                }
                catch (Exception)
                {
                    return new List<SearchOcrRecord>();
                }
            }
        }

        /// <summary>Loads, changes and saves in one go, so two changes at once can't lose either.</summary>
        public void Update(Action<List<SearchOcrRecord>> change)
        {
            lock (Lock)
            {
                var records = Load();
                change(records);
                var temp = _path + ".tmp";
                _json.SerializeToFile(records, temp);
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
        }

        /// <summary>Keeps one record per video and track: a new search OCR of the same track replaces the old record.</summary>
        public void Add(SearchOcrRecord record) => Update(list =>
        {
            list.RemoveAll(r => string.Equals(r.VideoPath, record.VideoPath, StringComparison.OrdinalIgnoreCase) && r.StreamIndex == record.StreamIndex);
            list.Add(record);
        });

        /// <summary>Videos that moved (same moves the main state follows): their records and files move along.</summary>
        public void ApplyMoves(IEnumerable<MovedVideos.Move> moves) => Update(list =>
        {
            foreach (var m in moves)
            {
                foreach (var r in list.Where(r => string.Equals(r.VideoPath, m.From, StringComparison.OrdinalIgnoreCase)))
                {
                    r.VideoPath = m.To;
                    if (r.FilePath != null) r.FilePath = Path.Combine(Path.GetDirectoryName(m.To), Path.GetFileName(r.FilePath));
                }
            }
        });
    }
}
