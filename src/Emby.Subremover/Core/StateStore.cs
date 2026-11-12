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

        /// <summary>
        /// What SubRemover changed (one or two lines, see BackupNotes), shown in the restore list. Null for backups made
        /// before 0.7.7.
        /// </summary>
        public List<string> Changes { get; set; }
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

        /// <summary>Subtitle files checked for ads, as they were then, so later runs only read new or changed ones.</summary>
        public Dictionary<string, FileStamp> AdChecked { get; set; } = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The ad list and switches <see cref="AdChecked"/> was made with; a change checks every file again.</summary>
        public string AdRulesSignature { get; set; }

        private HashSet<string> _madeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Subtitle files SubRemover wrote (extracted or OCR), so they're never taken for files Emby downloaded. Saved as a
        /// list: Emby's JSON reader doesn't decode the text in a saved set, so each save doubled every backslash (0.7.0 to
        /// 0.7.9-alpha.4). Paths spoiled that way are mended as they're read.
        /// </summary>
        public List<string> MadeFiles
        {
            get => _madeFiles.ToList();
            set => _madeFiles = new HashSet<string>((value ?? new List<string>()).Select(SavedPaths.Mend), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The same files, for look-ups and changes. A method, so no JSON reader or writer ever sees it.</summary>
        public HashSet<string> MadeFileSet() => _madeFiles;

        /// <summary>Which OCR made each OCR file a run wrote. Files from before 0.7.7 have none, so they count as older.</summary>
        public Dictionary<string, OcrRecord> OcrFiles { get; set; } = new Dictionary<string, OcrRecord>(StringComparer.OrdinalIgnoreCase);

        public bool OcrFileIsCurrent(string path, OcrModels chosen) =>
            OcrFiles.TryGetValue(path, out var record) && OcrVersion.IsCurrent(record, chosen);

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

        /// <summary>
        /// For a test-mode run: the lists a real run with these settings would use, so the test shows what that run
        /// would do. This state isn't changed.
        /// </summary>
        public SubremoverState PreviewFor(string signature, bool recheckEverything)
        {
            var copy = new SubremoverState
            {
                SettingsSignature = SettingsSignature,
                Processed = new Dictionary<string, FileStamp>(Processed, StringComparer.OrdinalIgnoreCase),
                Restored = new List<string>(Restored),
                // Copies: a test run may point them at moved videos, and that mustn't reach the real list.
                Backups = Backups.Select(b => new BackupEntry { OriginalPath = b.OriginalPath, BackupPath = b.BackupPath, CreatedUtc = b.CreatedUtc, Changes = b.Changes }).ToList(),
                MadeFiles = MadeFiles,
                OcrFiles = new Dictionary<string, OcrRecord>(OcrFiles, StringComparer.OrdinalIgnoreCase)
            };
            if (recheckEverything) copy.ForgetEverything();
            copy.ApplySettingsSignature(signature);
            return copy;
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
                SavedPaths.ShrinkIfSpoiled(_path, f =>
                {
                    var test = _json.DeserializeFromFile<SubremoverState>(f);
                    return test?.Processed != null && test.Backups != null;
                });
                var state = _json.DeserializeFromFile<SubremoverState>(_path) ?? new SubremoverState();
                state.Processed = new Dictionary<string, FileStamp>(state.Processed ?? new Dictionary<string, FileStamp>(), StringComparer.OrdinalIgnoreCase);
                state.Backups = state.Backups ?? new List<BackupEntry>();
                state.Restored = state.Restored ?? new List<string>();
                state.OcrFiles = new Dictionary<string, OcrRecord>(state.OcrFiles ?? new Dictionary<string, OcrRecord>(), StringComparer.OrdinalIgnoreCase);
                state.AdChecked = new Dictionary<string, FileStamp>(state.AdChecked ?? new Dictionary<string, FileStamp>(), StringComparer.OrdinalIgnoreCase);
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
