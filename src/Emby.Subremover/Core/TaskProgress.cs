using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// Where a hash-matched-subtitles or subtitle-timing run got to, saved as it goes, so a run that was stopped (or cut
    /// off by an Emby restart) can carry on instead of starting over. One file per task and mode, e.g.
    /// "Subremover.progress-hash-count.json"; deleted when the run finishes.
    /// </summary>
    public class TaskProgress
    {
        /// <summary>The settings the run was made with (libraries, path filter, languages...): another set starts over.</summary>
        public string Key { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime SavedUtc { get; set; }
        public int Total { get; set; }
        public string LogFile { get; set; }

        /// <summary>The videos already done.</summary>
        public List<string> Done { get; set; } = new List<string>();

        /// <summary>The run's tallies so far, by name.</summary>
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();

        public int Count(string name) => Counts != null && Counts.TryGetValue(name, out var n) ? n : 0;

        /// <summary>"1,234 of 3,026 videos done, stopped 16 Nov 09:40".</summary>
        public string Describe() =>
            $"{Done.Count:N0} of {Total:N0} videos done, stopped {SavedUtc.ToLocalTime():d MMM HH:mm}";
    }

    public class TaskProgressStore
    {
        private readonly string _folder;
        private readonly IJsonSerializer _json;
        private static readonly object Lock = new object();

        public TaskProgressStore(string configurationsFolder, IJsonSerializer json)
        {
            _folder = configurationsFolder;
            _json = json;
        }

        /// <param name="task">"hash-count", "hash-download", "timing-test" or "timing-fix".</param>
        public string PathFor(string task) => Path.Combine(_folder, $"Subremover.progress-{task}.json");

        public TaskProgress Load(string task)
        {
            lock (Lock)
            {
                try
                {
                    var path = PathFor(task);
                    return File.Exists(path) ? _json.DeserializeFromFile<TaskProgress>(path) : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>The saved progress when it was made with these settings, else null (and an old one is deleted).</summary>
        public TaskProgress LoadFor(string task, string key)
        {
            var saved = Load(task);
            if (saved == null) return null;
            if (saved.Key == key) return saved;
            Delete(task);
            return null;
        }

        public void Save(string task, TaskProgress progress)
        {
            lock (Lock)
            {
                try
                {
                    progress.SavedUtc = DateTime.UtcNow;
                    var path = PathFor(task);
                    _json.SerializeToFile(progress, path + ".tmp");
                    if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                    else File.Move(path + ".tmp", path);
                }
                catch (Exception)
                {
                    // Only lets a stopped run carry on; without it the next run starts over.
                }
            }
        }

        public void Delete(string task)
        {
            lock (Lock)
            {
                try { File.Delete(PathFor(task)); } catch (Exception) { }
            }
        }
    }
}
