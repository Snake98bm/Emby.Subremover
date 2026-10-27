using System;
using System.Collections.Generic;
using System.IO;
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

    public class SubremoverState
    {
        public Dictionary<string, FileStamp> Processed { get; set; } = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        public List<BackupEntry> Backups { get; set; } = new List<BackupEntry>();
        public string SettingsSignature { get; set; }
    }

    // Kept out of the plugin options so the settings page never has to load thousands of processed paths.
    public class StateStore
    {
        private readonly string _path;
        private readonly IJsonSerializer _json;
        private readonly object _lock = new object();

        public StateStore(string folder, IJsonSerializer json)
        {
            _path = Path.Combine(folder, "Emby.Subremover.state.json");
            _json = json;
        }

        public SubremoverState Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path)) return new SubremoverState();
                var state = _json.DeserializeFromFile<SubremoverState>(_path) ?? new SubremoverState();
                state.Processed = new Dictionary<string, FileStamp>(state.Processed ?? new Dictionary<string, FileStamp>(), StringComparer.OrdinalIgnoreCase);
                state.Backups = state.Backups ?? new List<BackupEntry>();
                return state;
            }
        }

        public void Save(SubremoverState state)
        {
            lock (_lock)
            {
                var temp = _path + ".tmp";
                _json.SerializeToFile(state, temp);
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
            }
        }
    }
}
