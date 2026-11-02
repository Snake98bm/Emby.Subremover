using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// New videos waiting for SubRemover: collected as Emby adds them, then handed to one run as a batch.
    /// </summary>
    public sealed class PendingVideos
    {
        private readonly object _lock = new object();
        private HashSet<string> _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void Add(string path)
        {
            lock (_lock) _paths.Add(path);
        }

        public int Count
        {
            get { lock (_lock) return _paths.Count; }
        }

        /// <returns>The waiting videos, now empty again; null when none were waiting.</returns>
        public HashSet<string> Take()
        {
            lock (_lock)
            {
                if (_paths.Count == 0) return null;
                var taken = _paths;
                _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return taken;
            }
        }

        public void AddRange(IEnumerable<string> paths)
        {
            lock (_lock) foreach (var p in paths ?? Enumerable.Empty<string>()) _paths.Add(p);
        }
    }
}
