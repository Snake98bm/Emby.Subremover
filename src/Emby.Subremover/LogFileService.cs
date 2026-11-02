using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Subremover
{
    /// <summary>A SubRemover log, for the "open in a new window" and "download" links on the settings page.</summary>
    [Route(Path, "GET", IsHidden = true)]
    [Unauthenticated] // a new browser window has no Emby login; the secret key in the link guards it instead
    public class GetSubremoverLog
    {
        public const string Path = "/SubRemover/Log";

        public string Name { get; set; }
        public string Key { get; set; }
        public bool Download { get; set; }

        /// <summary>The link to a log, as shown on the settings page.</summary>
        public static string Link(string name, string key, bool download) =>
            "/emby" + Path + "?Name=" + Uri.EscapeDataString(name) + "&Key=" + key + (download ? "&Download=true" : "");
    }

    public class LogFileService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _results;
        private readonly string _logDirectory;

        public LogFileService(IHttpResultFactory results, IApplicationPaths appPaths)
        {
            _results = results;
            _logDirectory = appPaths.LogDirectoryPath;
        }

        public IRequest Request { get; set; }

        public object Get(GetSubremoverLog request)
        {
            if (Plugin.Instance == null || !Plugin.Instance.LogLinkKey.IsValid(request.Key, DateTime.UtcNow))
                throw new SecurityException("This link has expired. Open SubRemover's settings page again for a new one.");

            // Only SubRemover's own logs, by exact name: no paths, no other files.
            if (string.IsNullOrEmpty(request.Name) || !RunLog.ListFiles(_logDirectory).Contains(request.Name))
                throw new FileNotFoundException("No SubRemover log with that name.");

            var headers = new Dictionary<string, string>();
            if (request.Download) headers["Content-Disposition"] = "attachment; filename=\"" + request.Name + "\"";

            var text = RunLog.ReadAll(_logDirectory, request.Name);
            return _results.GetResult(Request, new MemoryStream(Encoding.UTF8.GetBytes(text)), "text/plain; charset=utf-8", headers);
        }
    }
}
