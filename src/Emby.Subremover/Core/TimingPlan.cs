using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover.Core
{
    /// <summary>One subtitle file as a Scan run measured it, and what Fix should do with it.</summary>
    public sealed class PlannedFile
    {
        public string Path { get; set; }

        /// <summary>Size and time of the file when it was measured: a file changed since is measured again.</summary>
        public FileStamp Stamp { get; set; }

        /// <summary>What Fix does with it (Keep, Shift, Delete, LeaveAlone).</summary>
        public TimingAction Action { get; set; }

        public TimingVerdict? Verdict { get; set; }

        /// <summary>What to add to every time (Shift only).</summary>
        public long OffsetMs { get; set; }

        public long BestOffsetMs { get; set; }
        public double Confidence { get; set; }

        /// <summary>The best offset of the start, middle and end of the file.</summary>
        public long[] PartOffsetsMs { get; set; }

        /// <summary>In words: "in sync", "off by +2.30 s", "jumps +14.00 s near the end"...</summary>
        public string Needs { get; set; }

        public string Language { get; set; }
        public string Special { get; set; }
        public int Cues { get; set; }
        public bool IsCopy { get; set; }
        public string KeptInstead { get; set; }
        public bool KeptInSync { get; set; }

        internal static PlannedFile From(MeasuredSubtitle sub, FileStamp stamp) => new PlannedFile
        {
            Path = sub.Path,
            Stamp = stamp,
            Action = sub.Action,
            Verdict = sub.Result?.Verdict,
            OffsetMs = sub.Result?.OffsetMs ?? 0,
            BestOffsetMs = sub.Result?.BestOffsetMs ?? 0,
            Confidence = Math.Round(sub.Result?.Confidence ?? 0, 2),
            PartOffsetsMs = sub.Result?.PartOffsetsMs,
            Needs = sub.IsCopy ? "copy of the built-in subtitle, in sync" : sub.Result == null ? $"only {sub.Cues} cues, not measured" : sub.Result.Needs(),
            Language = sub.Language,
            Special = sub.Special,
            Cues = sub.Cues,
            IsCopy = sub.IsCopy,
            KeptInstead = sub.KeptInstead,
            KeptInSync = sub.KeptInSync
        };

        /// <summary>The measurement back, as if it had just been made.</summary>
        internal TimingResult Result() => Verdict == null ? null : new TimingResult
        {
            Verdict = Verdict.Value,
            OffsetMs = OffsetMs,
            BestOffsetMs = BestOffsetMs,
            Confidence = Confidence,
            PartOffsetsMs = PartOffsetsMs ?? new long[0]
        };
    }

    /// <summary>The subtitle files of one video, as a Scan run found them.</summary>
    public sealed class PlannedVideo
    {
        public string VideoPath { get; set; }

        /// <summary>The video when it was measured: another copy of it (another cut) means measuring again.</summary>
        public FileStamp VideoStamp { get; set; }

        public DateTime ScannedUtc { get; set; }
        public double AudioMinutes { get; set; }

        /// <summary>The measured audio track's language ("eng"; "und" when untagged); null in scans made before alpha.13.</summary>
        public string AudioLanguage { get; set; }
        public List<PlannedFile> Files { get; set; } = new List<PlannedFile>();
    }

    /// <summary>
    /// What the last "Fix subtitle file timing" Scan run found for each video: per subtitle file, how far off it is and
    /// what Fix will do. Fix uses it straight away instead of reading every video's audio again, and only measures a video
    /// again when the video or one of its .srt files changed (or a file came or went) since the scan, or the settings that
    /// decide what's done changed. Saved as Subremover.timing-plan.json; readable, so it also shows what a scan found.
    /// </summary>
    public sealed class TimingPlan
    {
        /// <summary>The measuring version and settings the plan was made with: another set ignores it.</summary>
        public string Signature { get; set; }

        public DateTime SavedUtc { get; set; }
        public List<PlannedVideo> Videos { get; set; } = new List<PlannedVideo>();

        private Dictionary<string, PlannedVideo> _byPath;

        private Dictionary<string, PlannedVideo> ByPath
        {
            get
            {
                if (_byPath == null)
                {
                    _byPath = new Dictionary<string, PlannedVideo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in Videos ?? new List<PlannedVideo>())
                        if (v?.VideoPath != null) _byPath[v.VideoPath] = v;
                }
                return _byPath;
            }
        }

        public int VideoCount() { lock (this) return ByPath.Count; }

        public void Put(PlannedVideo video)
        {
            lock (this)
            {
                ByPath[video.VideoPath] = video;
                Videos = ByPath.Values.ToList();
            }
        }

        public void Remove(string videoPath)
        {
            lock (this)
            {
                if (ByPath.Remove(videoPath)) Videos = ByPath.Values.ToList();
            }
        }

        public PlannedVideo Get(string videoPath)
        {
            lock (this) return ByPath.TryGetValue(videoPath, out var v) ? v : null;
        }

        /// <summary>Forget the videos that aren't in <paramref name="videoPaths"/> any more (gone, or out of the libraries).</summary>
        public void KeepOnly(IEnumerable<string> videoPaths)
        {
            var keep = new HashSet<string>(videoPaths, StringComparer.OrdinalIgnoreCase);
            lock (this)
            {
                foreach (var path in ByPath.Keys.Where(p => !keep.Contains(p)).ToList()) ByPath.Remove(path);
                Videos = ByPath.Values.ToList();
            }
        }

        /// <summary>
        /// The saved plan of a video when it still fits: the same video file, and the same .srt files (<paramref name="paths"/>),
        /// none changed since they were measured. Null means measure it again.
        /// </summary>
        public PlannedVideo Usable(string videoPath, IList<string> paths, Func<string, FileStamp> stampOf)
        {
            var planned = Get(videoPath);
            if (planned?.Files == null) return null;
            if (planned.VideoStamp == null || !planned.VideoStamp.Matches(stampOf(videoPath))) return null;
            if (planned.Files.Count != paths.Count) return null;
            var byPath = planned.Files.Where(f => f?.Path != null).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!byPath.TryGetValue(path, out var file) || file.Stamp == null || !file.Stamp.Matches(stampOf(path))) return null;
            }
            return planned;
        }
    }

    public sealed class TimingPlanStore
    {
        public const string FileName = "Subremover.timing-plan.json";
        private readonly string _path;
        private readonly IJsonSerializer _json;
        private static readonly object Lock = new object();

        public TimingPlanStore(string configurationsFolder, IJsonSerializer json)
        {
            _path = Path.Combine(configurationsFolder, FileName);
            _json = json;
        }

        public string FilePath => _path;

        /// <summary>The saved plan when it was made with <paramref name="signature"/>, else a new empty one.</summary>
        public TimingPlan LoadFor(string signature)
        {
            lock (Lock)
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        var plan = _json.DeserializeFromFile<TimingPlan>(_path);
                        if (plan != null && plan.Signature == signature) return plan;
                    }
                }
                catch (Exception)
                {
                    // Unreadable: Fix just measures everything, and the next scan writes a new one.
                }
                return new TimingPlan { Signature = signature };
            }
        }

        public void Save(TimingPlan plan)
        {
            lock (Lock)
            lock (plan)
            {
                try
                {
                    plan.SavedUtc = DateTime.UtcNow;
                    _json.SerializeToFile(plan, _path + ".tmp");
                    if (File.Exists(_path)) File.Replace(_path + ".tmp", _path, null);
                    else File.Move(_path + ".tmp", _path);
                }
                catch (Exception)
                {
                    // Only saves Fix from measuring again.
                }
            }
        }
    }
}
