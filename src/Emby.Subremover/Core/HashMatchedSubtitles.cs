using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

// The same file is in AudioRemover (Core/HashMatchedSubtitles.cs), with its own namespace: whichever plugin changes a
// video first does this step. Keep the two copies alike.
namespace Emby.Subremover.Core
{
    /// <summary>
    /// The hash OpenSubtitles (and Emby's subtitle search) uses for a "perfect match": the file size plus every 8-byte
    /// number in its first and last 64 KB. Rewriting a video changes it, so a perfect match is only found before.
    /// </summary>
    public static class OpenSubtitlesHash
    {
        private const int Chunk = 64 * 1024;

        public static string Compute(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
            {
                return Compute(stream);
            }
        }

        public static string Compute(Stream stream)
        {
            var size = stream.Length;
            ulong hash = (ulong)size;
            hash += SumOf(stream, 0, (int)Math.Min(Chunk, size));
            hash += SumOf(stream, Math.Max(0, size - Chunk), (int)Math.Min(Chunk, size));
            return hash.ToString("x16");
        }

        private static ulong SumOf(Stream stream, long offset, int length)
        {
            var buffer = new byte[length];
            stream.Position = offset;
            var read = 0;
            while (read < length)
            {
                var n = stream.Read(buffer, read, length - read);
                if (n <= 0) break;
                read += n;
            }
            ulong sum = 0;
            for (var i = 0; i + 8 <= read; i += 8) sum += BitConverter.ToUInt64(buffer, i);
            return sum;
        }
    }

    /// <summary>A video's hash from before SubRemover or AudioRemover first changed it.</summary>
    public class OriginalHash
    {
        public string Hash { get; set; }
        public long Size { get; set; }
        public DateTime CheckedUtc { get; set; }

        /// <summary>"SubRemover" or "AudioRemover": the plugin that was about to change the video.</summary>
        public string CheckedBy { get; set; }

        /// <summary>Subtitle files downloaded because they matched this hash.</summary>
        public List<string> Downloaded { get; set; } = new List<string>();
    }

    /// <summary>
    /// "Subremover.hashes.json" in Emby's plugin settings folder: video path -> its original hash. Shared by SubRemover and
    /// AudioRemover, so a video is looked up once, before whichever of them changes it first.
    /// </summary>
    public class OriginalHashStore
    {
        public const string FileName = "Subremover.hashes.json";
        private static readonly object InProcess = new object();
        private readonly string _path;
        private readonly IJsonSerializer _json;

        public OriginalHashStore(string configurationsFolder, IJsonSerializer json)
        {
            _path = Path.Combine(configurationsFolder, FileName);
            _json = json;
        }

        public bool Has(string videoPath) => Load().ContainsKey(videoPath);

        /// <summary>Subtitle files downloaded on this UTC day (the day providers such as OpenSubtitles count by), by either plugin.</summary>
        public int DownloadsOn(DateTime utcDay) =>
            Load().Values.Where(r => r.CheckedUtc.Date == utcDay.Date).Sum(r => r.Downloaded?.Count ?? 0);

        public Dictionary<string, OriginalHash> Load()
        {
            lock (InProcess)
            {
                try
                {
                    var saved = File.Exists(_path) ? _json.DeserializeFromFile<Dictionary<string, OriginalHash>>(_path) : null;
                    return new Dictionary<string, OriginalHash>(saved ?? new Dictionary<string, OriginalHash>(), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return new Dictionary<string, OriginalHash>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public void Add(string videoPath, OriginalHash record)
        {
            lock (InProcess)
            {
                // The other plugin may write at the same moment: a lock file both of them open alone.
                using (OpenLock())
                {
                    var all = Load();
                    all[videoPath] = record;
                    var temp = _path + ".tmp";
                    _json.SerializeToFile(all, temp);
                    if (File.Exists(_path)) File.Replace(temp, _path, null);
                    else File.Move(temp, _path);
                }
            }
        }

        private FileStream OpenLock()
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    public enum HashCheck
    {
        /// <summary>Looked up (or nothing to look up): the video may be changed.</summary>
        Done,

        /// <summary>The subtitle search failed: leave the video unchanged so a later run can still find a perfect match.</summary>
        TryLater
    }

    public enum HashMode
    {
        /// <summary>Test mode of a run that changes videos: only log the languages it would look up. Asks no provider.</summary>
        LogOnly,

        /// <summary>"Count hash-matched subtitles": asks the providers, downloads nothing and saves no hash.</summary>
        CountOnly,

        /// <summary>Downloads the best perfect match per language and saves the video's original hash.</summary>
        Download
    }

    /// <summary>What a look-up found.</summary>
    public class HashLookup
    {
        public HashCheck Check { get; set; } = HashCheck.Done;

        /// <summary>Languages with a perfect match (downloaded, or in count mode: would be).</summary>
        public List<string> Found { get; } = new List<string>();

        /// <summary>The files saved.</summary>
        public List<string> Downloaded { get; } = new List<string>();

        /// <summary>TryLater because today's download limit is used up, not because a search failed.</summary>
        public bool LimitReached { get; set; }
    }

    /// <summary>
    /// "Download hash-matched subtitles before changing a video": before a video is first rewritten, asks Emby's subtitle
    /// providers (such as OpenSubtitles) for subtitles that are a perfect match for the file as it is, in each kept language
    /// with no subtitle file and no embedded text subtitle, and saves the best one the way Emby's own search does.
    /// </summary>
    public class HashMatchedSubtitles
    {
        /// <summary>SubRemover's own OCR offer in Emby's subtitle search; never a perfect match for a download.</summary>
        public const string OcrProviderName = "SubRemover OCR";

        // Providers limit how fast they may be asked; videos checked side by side take turns.
        private static readonly SemaphoreSlim OneAtATime = new SemaphoreSlim(1, 1);

        // After a failed search (often the provider's own daily limit) nothing is asked for an hour, so the rest of a run
        // doesn't send a failing request for every video.
        private static DateTime _pausedUntilUtc;
        private static DateTime _limitLoggedFor;

        private readonly ISubtitleManager _subtitles;
        private readonly OriginalHashStore _store;
        private readonly ILogger _log;
        private readonly string _plugin;

        public HashMatchedSubtitles(ISubtitleManager subtitles, OriginalHashStore store, ILogger log, string pluginName)
        {
            _subtitles = subtitles;
            _store = store;
            _log = log;
            _plugin = pluginName;
        }

        public bool AlreadyChecked(string videoPath) => _store.Has(videoPath);

        /// <summary>
        /// Languages worth a search. None when the original video has a built-in text subtitle (.srt-like) in a kept
        /// language or with no language set: that one is already exact. Otherwise each kept language with no subtitle file
        /// next to the video, so a video with only picture subtitles, or none, gets a search.
        /// </summary>
        public static List<string> LanguagesToSearch(IEnumerable<string> keepLanguages, IEnumerable<string> embeddedTextLanguages,
            IEnumerable<string> fileLanguages)
        {
            var keep = keepLanguages.Where(l => !string.IsNullOrEmpty(l) && l != "und").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (embeddedTextLanguages.Any(l => string.IsNullOrEmpty(l) || l == "und" || keep.Contains(l, StringComparer.OrdinalIgnoreCase)))
                return new List<string>();
            var files = new HashSet<string>(fileLanguages.Where(l => !string.IsNullOrEmpty(l)), StringComparer.OrdinalIgnoreCase);
            return keep.Where(l => !files.Contains(l)).ToList();
        }

        /// <summary>
        /// Looks the video up (see <see cref="HashMode"/>). In Download mode the video's hash is saved even when there's
        /// nothing to look up, so it's never looked at again.
        /// </summary>
        /// <param name="downloadsPerDay">Download mode: at most this many files a UTC day, counted across both plugins; 0 = no limit.
        /// A video that could go over it waits (TryLater) for the next day.</param>
        public async Task<HashLookup> Lookup(BaseItem item, IList<string> languages, HashMode mode, CancellationToken ct, int downloadsPerDay = 0)
        {
            var path = item.Path;
            var result = new HashLookup();
            var record = new OriginalHash { CheckedUtc = DateTime.UtcNow, CheckedBy = _plugin };
            if (mode == HashMode.CountOnly && languages.Count == 0) return result;
            try
            {
                record.Hash = OpenSubtitlesHash.Compute(path);
                record.Size = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                _log.Warn($"{_plugin}: couldn't read {path} for its hash ({ex.Message}); leaving it for the next run");
                result.Check = HashCheck.TryLater;
                return result;
            }

            // A full test run would ask the provider about thousands of videos and use up its daily limit.
            if (mode == HashMode.LogOnly)
            {
                if (languages.Count > 0)
                    _log.Info($"{_plugin} [TEST MODE - no changes]: would look for hash-matched {string.Join(", ", languages)} subtitles " +
                              $"for {path} (hash {record.Hash}) before changing it");
                return result;
            }

            if (languages.Count > 0 && !HasProvider(item))
            {
                _log.Debug($"{_plugin}: no subtitle downloader is set up in Emby, so no hash-matched subtitles for {path}");
                languages = new List<string>();
            }

            var finished = false;
            await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (languages.Count > 0 && DateTime.UtcNow < _pausedUntilUtc)
                {
                    _log.Debug($"{_plugin}: subtitle searches are paused after a failure until {_pausedUntilUtc.ToLocalTime():HH:mm}; leaving {path} unchanged for the next run");
                    result.Check = HashCheck.TryLater;
                    return result;
                }
                if (mode == HashMode.Download && downloadsPerDay > 0 && languages.Count > 0)
                {
                    var today = DateTime.UtcNow.Date;
                    var used = _store.DownloadsOn(today);
                    if (used + languages.Count > downloadsPerDay)
                    {
                        if (_limitLoggedFor != today)
                        {
                            _limitLoggedFor = today;
                            _log.Info($"{_plugin}: {used} of the {downloadsPerDay} hash-matched subtitle downloads allowed today are used; " +
                                      "videos that still need a look-up are left unchanged until tomorrow (midnight UTC)");
                        }
                        _log.Debug($"{_plugin}: daily download limit reached; leaving {path} unchanged for the next run");
                        result.Check = HashCheck.TryLater;
                        result.LimitReached = true;
                        return result;
                    }
                }

                foreach (var language in languages)
                {
                    var best = (await _subtitles.SearchSubtitles(item, language, true, false, null, ct).ConfigureAwait(false) ?? Array.Empty<MediaBrowser.Model.Providers.RemoteSubtitleInfo>())
                        .Where(r => r.IsHashMatch == true && r.IsForced != true && r.ProviderName != OcrProviderName)
                        .OrderBy(r => r.IsHearingImpaired == true)
                        .ThenByDescending(r => r.DownloadCount ?? 0)
                        .FirstOrDefault();
                    // A little room between requests: providers turn away clients that ask too fast.
                    await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    if (best == null)
                    {
                        _log.Debug($"{_plugin}: no hash-matched {language} subtitle for {path} (hash {record.Hash})");
                        continue;
                    }

                    result.Found.Add(language);
                    var what = $"{best.ProviderName} {language} subtitle \"{best.Name}\"{(best.IsHearingImpaired == true ? " (SDH)" : "")}";
                    if (mode == HashMode.CountOnly)
                    {
                        _log.Info($"{_plugin}: found the hash-matched {what} for {path} (not downloaded: count only)");
                        continue;
                    }
                    var saved = await _subtitles.DownloadSubtitles(item, best.Id, ct).ConfigureAwait(false);
                    _log.Info($"{_plugin}: downloaded the hash-matched {what} for {path}" + (string.IsNullOrEmpty(saved) ? "" : " -> " + saved));
                    if (!string.IsNullOrEmpty(saved)) record.Downloaded.Add(saved);
                }
                finished = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Once the video is changed a perfect match can't be found any more, so it waits.
                _pausedUntilUtc = DateTime.UtcNow.AddHours(1);
                _log.Warn($"{_plugin}: the hash-matched subtitle search for {path} failed ({ex.Message}); leaving the video unchanged " +
                          "for the next run, and asking nothing more for an hour (the provider's daily limit may be used up)");
                result.Check = HashCheck.TryLater;
                return result;
            }
            finally
            {
                // Saved before the next video takes its turn, so the daily count is right for it.
                if (mode == HashMode.Download && finished) _store.Add(path, record);
                OneAtATime.Release();
            }

            result.Downloaded.AddRange(record.Downloaded);
            return result;
        }

        private bool HasProvider(BaseItem item)
        {
            try
            {
                var providers = _subtitles.GetSupportedProviders(item);
                return providers == null || providers.Any(p => p.Name != OcrProviderName);
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
