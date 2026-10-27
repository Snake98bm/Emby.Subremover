using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Logging;

namespace Emby.Subremover
{
    /// <summary>
    /// Writes one run's full detail to its own file in Emby's log folder. Errors also go to the main Emby log,
    /// everything else stays in the run file.
    /// </summary>
    public sealed class RunLog : ILogger, IDisposable
    {
        public const int KeepFiles = 30;

        private readonly ILogger _emby;
        private readonly StreamWriter _writer;
        private readonly object _lock = new object();

        public RunLog(string logDirectory, string prefix, ILogger emby)
        {
            _emby = emby;
            Directory.CreateDirectory(logDirectory);
            DeleteOldFiles(logDirectory, prefix, KeepFiles - 1);

            FilePath = Path.Combine(logDirectory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        public string FilePath { get; }

        public string FileName => Path.GetFileName(FilePath);

        /// <summary>A line for both the run file and the main Emby log.</summary>
        public void Summary(string message, params object[] paramList)
        {
            Write(LogSeverity.Info, message, paramList);
            _emby.Info(message, paramList);
        }

        public void Info(string message, params object[] paramList) => Write(LogSeverity.Info, message, paramList);
        public void Debug(string message, params object[] paramList) => Write(LogSeverity.Debug, message, paramList);
        public void Warn(string message, params object[] paramList) => Write(LogSeverity.Warn, message, paramList);

        public void Error(string message, params object[] paramList)
        {
            Write(LogSeverity.Error, message, paramList);
            _emby.Error(message, paramList);
        }

        public void Fatal(string message, params object[] paramList)
        {
            Write(LogSeverity.Fatal, message, paramList);
            _emby.Fatal(message, paramList);
        }

        public void ErrorException(string message, Exception exception, params object[] paramList)
        {
            Write(LogSeverity.Error, message + Environment.NewLine + exception, paramList);
            _emby.ErrorException(message, exception, paramList);
        }

        public void FatalException(string message, Exception exception, params object[] paramList)
        {
            Write(LogSeverity.Fatal, message + Environment.NewLine + exception, paramList);
            _emby.FatalException(message, exception, paramList);
        }

        public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) =>
            Log(severity, message + Environment.NewLine + additionalContent);

        public void Log(LogSeverity severity, string message, params object[] paramList)
        {
            switch (severity)
            {
                case LogSeverity.Error: Error(message, paramList); break;
                case LogSeverity.Fatal: Fatal(message, paramList); break;
                default: Write(severity, message, paramList); break;
            }
        }

        public void Log(LogSeverity severity, ReadOnlyMemory<char> message) => Log(severity, message.ToString());
        public void Error(ReadOnlyMemory<char> message) => Error(message.ToString());
        public void Warn(ReadOnlyMemory<char> message) => Warn(message.ToString());
        public void Info(ReadOnlyMemory<char> message) => Info(message.ToString());
        public void Debug(ReadOnlyMemory<char> message) => Debug(message.ToString());

        public void Dispose()
        {
            lock (_lock) _writer.Dispose();
        }

        private void Write(LogSeverity severity, string message, object[] paramList)
        {
            string text;
            try
            {
                text = paramList == null || paramList.Length == 0 ? message : string.Format(CultureInfo.InvariantCulture, message, paramList);
            }
            catch (FormatException)
            {
                text = message + " " + string.Join(" ", paramList);
            }

            lock (_lock)
            {
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {severity,-5} {text}");
            }
        }

        /// <summary>Run log file names, newest first.</summary>
        public static List<string> ListFiles(string directory)
        {
            if (!Directory.Exists(directory)) return new List<string>();
            return new DirectoryInfo(directory).GetFiles("subremover-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.Name)
                .ToList();
        }

        /// <summary>
        /// Reads a run log for display. Only names that are actually run logs in <paramref name="directory"/> are read,
        /// because the name comes from the browser.
        /// </summary>
        public static string ReadForDisplay(string directory, string fileName, int maxChars = 200_000)
        {
            if (string.IsNullOrEmpty(fileName) || !ListFiles(directory).Contains(fileName)) return "No run log yet. Click Run now to create one.";

            string text;
            using (var stream = new FileStream(Path.Combine(directory, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = reader.ReadToEnd();
            }
            return text.Length <= maxChars
                ? text
                : "… (start of the log cut off, open the file under Settings → Logs for all of it)" + Environment.NewLine + text.Substring(text.Length - maxChars);
        }

        internal static void DeleteOldFiles(string directory, string prefix, int keep)
        {
            var old = new DirectoryInfo(directory).GetFiles(prefix + "-*.txt")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(keep);
            foreach (var file in old)
            {
                try { file.Delete(); } catch { }
            }
        }
    }
}
