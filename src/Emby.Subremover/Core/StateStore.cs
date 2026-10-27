using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover.Core
{
    public class FileStamp
    {
        public long Size { get; set; }
        public long LastWriteTicksUtc { get; set; }

        public static FileStamp Of(string path)
        {
            var info = new FileInfo(path);
            return new FileStamp { Size = info.Length, LastWriteTicksUtc = info.LastWriteTimeUtc.Ticks };
        }

        public bool Matches(FileStamp other) =>
            other != null && other.Size == Size && other.LastWriteTicksUtc == LastWriteTicksUtc;
    }

    public class BackupEntry
    {
        public string OriginalPath { get; set; }
        public string BackupPath { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public class RunSummary
    {
        public DateTime FinishedUtc { get; set; }
        public bool DryRun { get; set; }
        public int Changed { get; set; }
        public int WouldChange { get; set; }
        public int NothingToDo { get; set; }
        public int AlreadyDone { get; set; }
        public int Failed { get; set; }
        public string LogFile { get; set; }

        /// <summary>Set when the run stopped before checking any videos, e.g. no libraries selected.</summary>
        public string Problem { get; set; }
    }

    public class SubremoverState
    {
        public RunSummary LastRun { get; set; }

        public Dictionary<string, FileStamp> Processed { get; set; } = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        public List<BackupEntry> Backups { get; set; } = new List<BackupEntry>();
        public string SettingsSignature { get; set; }

        /// <summary>Videos put back by the restore task. They are left alone until "Re-check every video" is used.</summary>
        public List<string> Restored { get; set; } = new List<string>();

        /// <summary>The video being changed right now, so leftovers can be cleaned up if Emby stops mid-way.</summary>
        public string InProgress { get; set; }

        public bool IsRestored(string path) => Restored.Contains(path, StringComparer.OrdinalIgnoreCase);

        public void MarkRestored(string path)
        {
            if (!IsRestored(path)) Restored.Add(path);
        }

        /// <summary>A settings change means every video needs checking again, except ones the user restored on purpose.</summary>
        /// <returns>true when videos had been checked before and now will be again.</returns>
        public bool ApplySettingsSignature(string signature)
        {
            if (SettingsSignature == signature) return false;
            var hadProcessed = Processed.Count > 0;
            Processed.Clear();
            SettingsSignature = signature;
            return hadProcessed;
        }

        public void ForgetEverything()
        {
            Processed.Clear();
            Restored.Clear();
        }
    }

    // Kept out of the plugin options so the settings page never has to load thousands of processed paths.
    public class StateStore
    {
        private readonly string _path;
        private readonly IJsonSerializer _json;

        // Static: the remove and restore tasks and the settings page each have their own StateStore for the same file.
        private static readonly object Lock = new object();

        public StateStore(string folder, IJsonSerializer json)
        {
            _path = Path.Combine(folder, "Emby.Subremover.state.json");
            _json = json;
        }

        public SubremoverState Load()
        {
            lock (Lock)
            {
                if (!File.Exists(_path)) return new SubremoverState();
                var state = _json.DeserializeFromFile<SubremoverState>(_path) ?? new SubremoverState();
                state.Processed = new Dictionary<string, FileStamp>(state.Processed ?? new Dictionary<string, FileStamp>(), StringComparer.OrdinalIgnoreCase);
                state.Backups = state.Backups ?? new List<BackupEntry>();
                state.Restored = state.Restored ?? new List<string>();
                return state;
            }
        }

        public void Save(SubremoverState state)
        {
            lock (Lock)
            {
                var temp = _path + ".tmp";
                _json.SerializeToFile(state, temp);
                // One step, so a crash never leaves the record (and the list of backups) half-written or missing.
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
        }
    }
}
