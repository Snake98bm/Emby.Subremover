using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// How far a run got, so a run that was stopped or cut off by an Emby restart can carry on where it stopped
    /// ("Continue an interrupted run where it stopped"). Deleted when a run finishes.
    /// </summary>
    public class RunCheckpoint
    {
        /// <summary>The settings the run used (see <see cref="KeyFor"/>): a run with other settings starts over.</summary>
        public string Key { get; set; }
        public bool DryRun { get; set; }
        public DateTime StartedUtc { get; set; }
        public string LogFile { get; set; }

        /// <summary>Videos already checked (and changed, when it was a real run).</summary>
        public List<string> Checked { get; set; } = new List<string>();

        /// <summary>Outcome counts so far, by outcome name.</summary>
        public Dictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();

        public RunTotals Totals { get; set; } = new RunTotals();

        /// <summary>The run's settings, Test mode and library and path choices: what decides which videos and what's done to them.</summary>
        public static string KeyFor(string settingsSignature, bool dryRun, string libraries, string onlyPathContains) =>
            string.Join("||", settingsSignature, dryRun, libraries ?? "", onlyPathContains ?? "");
    }

    public class CheckpointStore
    {
        private static readonly object Lock = new object();
        private readonly string _path;
        private readonly IJsonSerializer _json;

        public CheckpointStore(string folder, IJsonSerializer json)
        {
            _path = Path.Combine(folder, "Emby.Subremover.progress.json");
            _json = json;
        }

        public bool Exists => File.Exists(_path);

        public RunCheckpoint Load()
        {
            lock (Lock)
            {
                try
                {
                    return File.Exists(_path) ? _json.DeserializeFromFile<RunCheckpoint>(_path) : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public void Save(RunCheckpoint checkpoint)
        {
            lock (Lock)
            {
                var temp = _path + ".tmp";
                _json.SerializeToFile(checkpoint, temp);
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
        }

        public void Delete()
        {
            lock (Lock)
            {
                try { if (File.Exists(_path)) File.Delete(_path); } catch (Exception) { }
            }
        }
    }
}
