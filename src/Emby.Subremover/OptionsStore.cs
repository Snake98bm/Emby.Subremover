using System;
using System.IO;
using System.Text;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover
{
    /// <summary>
    /// Reads and writes the settings in the same "&lt;plugin name&gt;.json" file Emby's built-in settings page used,
    /// so settings saved by earlier versions carry over.
    /// </summary>
    /// <remarks>
    /// The tasks share one cached copy; the settings page always gets its own copy, because it fills in
    /// display-only data (lists, log text) that must never reach the settings file.
    /// </remarks>
    public class OptionsStore
    {
        private readonly IJsonSerializer _json;
        private readonly ILogger _log;
        private readonly object _lock = new object();
        private PluginOptions _options;

        public OptionsStore(string configurationsFolder, string pluginName, IJsonSerializer json, ILogger log)
        {
            FilePath = Path.Combine(configurationsFolder, pluginName + ".json");
            _json = json;
            _log = log;
        }

        public string FilePath { get; }

        public PluginOptions Get()
        {
            lock (_lock)
            {
                if (_options != null) return _options;
                _options = new PluginOptions();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        using (var stream = File.OpenRead(FilePath))
                        {
                            _options = (PluginOptions)new PluginOptions().DeserializeFromJsonStream(stream, _json);
                        }
                        _options.ClearDisplayOnlyFields();
                    }
                }
                catch (Exception ex)
                {
                    _log.ErrorException("SubRemover: could not read settings from {0}, using defaults", ex, FilePath);
                }
                return _options;
            }
        }

        public PluginOptions GetCopy()
        {
            lock (_lock)
            {
                return Clone(Get());
            }
        }

        public void Save(PluginOptions options)
        {
            lock (_lock)
            {
                var clean = Clone(options);
                clean.ClearDisplayOnlyFields();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
                {
                    _json.SerializeToStream(clean, stream, new JsonSerializerOptions { Indent = true });
                }
                _options = clean;
            }
        }

        private PluginOptions Clone(PluginOptions options)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(_json.SerializeToString(options))))
            {
                return (PluginOptions)new PluginOptions().DeserializeFromJsonStream(stream, _json);
            }
        }
    }
}
