using System;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover
{
    /// <summary>
    /// Reads and writes the settings in the same "&lt;plugin name&gt;.json" file Emby's built-in settings page used,
    /// so settings saved by earlier versions carry over.
    /// </summary>
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
                    }
                }
                catch (Exception ex)
                {
                    _log.ErrorException("Subremover: could not read settings from {0}, using defaults", ex, FilePath);
                }
                return _options;
            }
        }

        public void Save(PluginOptions options)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
                {
                    _json.SerializeToStream(options, stream, new JsonSerializerOptions { Indent = true });
                }
                _options = options;
            }
        }
    }
}
