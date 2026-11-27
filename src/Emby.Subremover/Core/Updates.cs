using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Serialization;

namespace Emby.Subremover.Core
{
    // ---- What GitHub's "latest release" API returns (only the parts used). The names are GitHub's.

#pragma warning disable IDE1006 // GitHub's JSON names
    public class GitHubRelease
    {
        public string tag_name { get; set; }
        public string html_url { get; set; }
        public bool draft { get; set; }
        public bool prerelease { get; set; }
        public string published_at { get; set; }
        public List<GitHubAsset> assets { get; set; }
    }

    public class GitHubAsset
    {
        public string name { get; set; }
        public long size { get; set; }
        public string browser_download_url { get; set; }

        /// <summary>"sha256:…", which GitHub adds to release files.</summary>
        public string digest { get; set; }
    }
#pragma warning restore IDE1006

    /// <summary>What the update check found and did, kept in subremover-update.json for the settings page.</summary>
    public class UpdateState
    {
        public DateTime? LastCheckUtc { get; set; }

        /// <summary>The newest release on GitHub at the last check, e.g. "1.0.7".</summary>
        public string LatestVersion { get; set; }
        public string ReleaseUrl { get; set; }

        /// <summary>The release that was put in the plugins folder, waiting for Emby to restart.</summary>
        public string InstalledVersion { get; set; }
        public DateTime? InstalledUtc { get; set; }

        /// <summary>The release Emby was restarted for, so it's only restarted once for each.</summary>
        public string RestartedFor { get; set; }

        /// <summary>Why the last check or install failed; null when it worked.</summary>
        public string Problem { get; set; }
    }

    internal sealed class UpdateStateStore
    {
        internal const string FileName = "subremover-update.json";
        private readonly string _path;
        private readonly IJsonSerializer _json;
        private readonly object _lock = new object();

        internal UpdateStateStore(string folder, IJsonSerializer json)
        {
            _path = Path.Combine(folder, FileName);
            _json = json;
        }

        internal UpdateState Load()
        {
            lock (_lock)
            {
                try { return File.Exists(_path) ? _json.DeserializeFromFile<UpdateState>(_path) ?? new UpdateState() : new UpdateState(); }
                catch (Exception) { return new UpdateState(); }
            }
        }

        internal void Save(UpdateState state)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var temp = _path + ".tmp";
                File.WriteAllText(temp, _json.SerializeToString(state));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(temp, _path);
            }
        }
    }

    /// <summary>Where releases come from: GitHub, or a stand-in in the tests.</summary>
    internal interface IReleaseSource
    {
        /// <summary>The newest release, or null when there is none.</summary>
        Task<GitHubRelease> Latest(CancellationToken cancellationToken);

        Task Download(string url, string toFile, long maxBytes, CancellationToken cancellationToken);
    }

    internal sealed class GitHubReleaseSource : IReleaseSource
    {
        internal const string Repository = "Snake98bm/Emby.Subremover";
        internal const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

        private static readonly HttpClient Http = CreateClient();
        private readonly IJsonSerializer _json;

        internal GitHubReleaseSource(IJsonSerializer json) => _json = json;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            // GitHub refuses requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Emby.Subremover", Plugin.VersionText.Split('-')[0]));
            return client;
        }

        public async Task<GitHubRelease> Latest(CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 404) return null; // no release yet
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return _json.DeserializeFromString<GitHubRelease>(text);
                }
            }
        }

        public async Task Download(string url, string toFile, long maxBytes, CancellationToken cancellationToken)
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"the download answered {(int)response.StatusCode} {response.ReasonPhrase}");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = File.Create(toFile))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > maxBytes) throw new InvalidDataException("the download is bigger than " + maxBytes + " bytes");
                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    internal enum UpdateOutcome
    {
        /// <summary>This is the newest release.</summary>
        UpToDate,
        /// <summary>A newer release is there; installing was off.</summary>
        Available,
        /// <summary>The newer release was put in the plugins folder now; Emby uses it after a restart.</summary>
        Installed,
        /// <summary>The newer release was already in the plugins folder, waiting for a restart.</summary>
        WaitingForRestart,
        /// <summary>Emby was restarted for this release but still runs the old one; nothing was done.</summary>
        StillOld,
        Failed
    }

    /// <summary>
    /// Checks GitHub for a newer SubRemover release and, when asked, puts its DLL in place of the running one. Emby only
    /// loads plugins when it starts, so the new version is used after the next restart.
    /// </summary>
    internal sealed class Updater
    {
        internal const string AssetName = "Emby.Subremover.dll";
        internal const string AssemblyName = "Emby.Subremover";
        internal const string Caption = "Updates";
        private const long MaxDownloadBytes = 100L * 1024 * 1024;

        private readonly IReleaseSource _source;
        private readonly UpdateStateStore _store;
        private readonly PluginVersion _running;
        private readonly string _pluginFile;
        private readonly string _workFolder;
        private readonly Func<DateTime> _utcNow;

        /// <param name="pluginFile">The DLL Emby loaded SubRemover from, which the update replaces.</param>
        /// <param name="workFolder">Where the download goes, and the copy of the replaced DLL.</param>
        internal Updater(IReleaseSource source, UpdateStateStore store, PluginVersion running, string pluginFile, string workFolder, Func<DateTime> utcNow = null)
        {
            _source = source;
            _store = store;
            _running = running;
            _pluginFile = pluginFile;
            _workFolder = workFolder;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>The copy of the DLL an update replaced, to put back by hand if the new one doesn't work.</summary>
        internal string PreviousCopy => Path.Combine(_workFolder, "previous", AssetName);

        /// <summary>The release found and, for <see cref="UpdateOutcome.Installed"/> and <see cref="UpdateOutcome.WaitingForRestart"/>, put in place.</summary>
        internal PluginVersion Latest { get; private set; }

        /// <param name="install">Put a newer release in place; otherwise only note that it's there.</param>
        /// <param name="manual">Clicked on the settings page: install even after a restart that didn't take.</param>
        internal async Task<UpdateOutcome> Run(bool install, bool manual, Action<string> log, CancellationToken cancellationToken)
        {
            var state = _store.Load();
            state.LastCheckUtc = _utcNow();
            try
            {
                UpdateInstaller.CleanUp(_pluginFile);

                var release = await _source.Latest(cancellationToken).ConfigureAwait(false);
                if (release == null || string.IsNullOrEmpty(release.tag_name) || release.draft || release.prerelease)
                    return Fail(state, "GitHub has no SubRemover release yet.", log);
                if (!PluginVersion.TryParse(release.tag_name, out var latest))
                    return Fail(state, $"The newest release's name '{release.tag_name}' isn't a version.", log);

                Latest = latest;
                state.LatestVersion = latest.ToString();
                state.ReleaseUrl = release.html_url;
                state.Problem = null;

                if (!latest.IsNewerThan(_running))
                {
                    log?.Invoke($"SubRemover v{_running} is up to date (newest release v{latest})");
                    state.InstalledVersion = null;
                    state.RestartedFor = null;
                    return Done(state, UpdateOutcome.UpToDate);
                }

                if (UpdateInstaller.FileHasVersion(_pluginFile, latest))
                {
                    if (state.RestartedFor == latest.ToString() && !manual)
                    {
                        state.Problem = $"Emby was restarted for v{latest}, but SubRemover v{_running} is still running. " +
                                        $"Check the plugins folder for a second SubRemover DLL; the new one is {_pluginFile}.";
                        log?.Invoke("SubRemover: " + state.Problem);
                        return Done(state, UpdateOutcome.StillOld);
                    }
                    state.InstalledVersion = latest.ToString();
                    return Done(state, UpdateOutcome.WaitingForRestart);
                }

                if (!install)
                {
                    log?.Invoke($"SubRemover v{latest} is available (running v{_running}); installing updates is off");
                    return Done(state, UpdateOutcome.Available);
                }

                var asset = release.assets?.FirstOrDefault(a => string.Equals(a.name, AssetName, StringComparison.OrdinalIgnoreCase));
                if (asset == null || string.IsNullOrEmpty(asset.browser_download_url))
                    return Fail(state, $"Release v{latest} has no {AssetName} to download.", log);

                Directory.CreateDirectory(_workFolder);
                var download = Path.Combine(_workFolder, AssetName + ".download");
                try
                {
                    log?.Invoke($"SubRemover: downloading v{latest} from {asset.browser_download_url}");
                    await _source.Download(asset.browser_download_url, download, MaxDownloadBytes, cancellationToken).ConfigureAwait(false);
                    var problem = UpdateInstaller.Verify(download, latest, asset.size, asset.digest);
                    if (problem != null) return Fail(state, $"The download of v{latest} was not installed: {problem}.", log);

                    cancellationToken.ThrowIfCancellationRequested();
                    UpdateInstaller.Swap(download, _pluginFile, PreviousCopy);
                }
                finally
                {
                    try { File.Delete(download); } catch (Exception) { }
                }

                state.InstalledVersion = latest.ToString();
                state.InstalledUtc = _utcNow();
                log?.Invoke($"SubRemover: installed v{latest} in {_pluginFile} (v{_running} copied to {PreviousCopy}); it starts working when Emby restarts");
                return Done(state, UpdateOutcome.Installed);
            }
            catch (OperationCanceledException)
            {
                _store.Save(state);
                throw;
            }
            catch (Exception ex)
            {
                return Fail(state, "The update check failed: " + ex.Message, log);
            }
        }

        private UpdateOutcome Done(UpdateState state, UpdateOutcome outcome)
        {
            _store.Save(state);
            return outcome;
        }

        private UpdateOutcome Fail(UpdateState state, string problem, Action<string> log)
        {
            state.Problem = problem;
            log?.Invoke("SubRemover: " + problem);
            _store.Save(state);
            return UpdateOutcome.Failed;
        }

        /// <summary>What the settings page says about updates.</summary>
        /// <param name="installs">Whether "Install updates automatically" is on.</param>
        internal static (string Text, ItemStatus Status, string Available) Describe(UpdateState state, PluginVersion running, bool installs)
        {
            var ok = ItemStatus.Succeeded;
            var warn = ItemStatus.Warning;
            var checkedAt = state?.LastCheckUtc == null ? null : " (checked " + When(state.LastCheckUtc.Value) + ")";
            if (state?.LastCheckUtc == null)
                return ($"Running v{running}. Not checked for updates yet" + (installs ? "; it checks once a day." : "."), ItemStatus.None, null);

            PluginVersion.TryParse(state.LatestVersion, out var latest);
            PluginVersion.TryParse(state.InstalledVersion, out var installed);

            if (installed != null && installed.IsNewerThan(running))
                return ($"v{installed} is installed and starts working when Emby restarts. Running v{running} until then.", warn, null);
            if (state.Problem != null)
                return (state.Problem + checkedAt, ItemStatus.Failed, latest != null && latest.IsNewerThan(running) ? latest.ToString() : null);
            if (latest != null && latest.IsNewerThan(running))
                return ($"v{latest} is available; running v{running}" + checkedAt + "." + (installs ? " It's installed at the next daily check, or click Install now." : " Click Install now to install it."), warn, latest.ToString());
            return ($"Up to date: running v{running}, the newest release" + checkedAt + ".", ok, null);
        }

        private static string When(DateTime utc) => utc.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    internal static class UpdateInstaller
    {
        /// <summary>Why the downloaded file isn't the release it should be, or null when it is.</summary>
        internal static string Verify(string file, PluginVersion expected, long expectedSize, string digest)
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0) return "the file is empty";
            if (expectedSize > 0 && info.Length != expectedSize) return $"it is {info.Length} bytes, GitHub lists {expectedSize}";

            if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var want = digest.Substring(7).Trim();
                string got;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(file))
                    got = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase)) return "its SHA-256 doesn't match the one GitHub lists";
            }

            AssemblyName name;
            try { name = System.Reflection.AssemblyName.GetAssemblyName(file); }
            catch (Exception) { return "it isn't a .NET DLL"; }
            if (!string.Equals(name.Name, Updater.AssemblyName, StringComparison.Ordinal)) return $"it is {name.Name}, not SubRemover";
            if (name.Version == null || !PluginVersion.From(name.Version).SameNumbers(expected))
                return $"it is version {name.Version}, not {expected}";
            return null;
        }

        /// <summary>Whether <paramref name="file"/> is SubRemover with the numbers of <paramref name="version"/>.</summary>
        internal static bool FileHasVersion(string file, PluginVersion version)
        {
            try
            {
                var name = System.Reflection.AssemblyName.GetAssemblyName(file);
                return name.Name == Updater.AssemblyName && name.Version != null && PluginVersion.From(name.Version).SameNumbers(version);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Puts <paramref name="newFile"/> where <paramref name="target"/> is. The running DLL is never written over: it is
        /// renamed out of the way (Linux and Windows both allow that for a loaded file) and the new one moved in, so a
        /// failure leaves the old one in place.
        /// </summary>
        /// <param name="previousCopy">Where a copy of the replaced DLL is kept.</param>
        internal static void Swap(string newFile, string target, string previousCopy)
        {
            if (!File.Exists(target)) throw new FileNotFoundException("SubRemover's DLL isn't where Emby loaded it from", target);

            Directory.CreateDirectory(Path.GetDirectoryName(previousCopy));
            File.Copy(target, previousCopy, true);

            // Staged next to the target first, so the last step is a rename on the same disk.
            var staged = target + ".new";
            var old = FreeOldName(target);
            File.Copy(newFile, staged, true);
            try
            {
                File.Move(target, old);
                try
                {
                    File.Move(staged, target);
                }
                catch (Exception)
                {
                    File.Move(old, target);
                    throw;
                }
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch (Exception) { }
            }

            // Windows keeps a loaded DLL locked; then it's deleted at the next check, after Emby restarted.
            try { File.Delete(old); } catch (Exception) { }
        }

        // "x.dll.old", or "x.dll.2.old" when an older one is still locked (a second update before Emby restarted, on Windows).
        private static IEnumerable<string> OldNames(string target) =>
            new[] { target + ".old" }.Concat(Enumerable.Range(2, 8).Select(i => target + "." + i.ToString(CultureInfo.InvariantCulture) + ".old"));

        private static string FreeOldName(string target)
        {
            foreach (var name in OldNames(target))
            {
                try { if (File.Exists(name)) File.Delete(name); } catch (Exception) { }
                if (!File.Exists(name)) return name;
            }
            throw new IOException("Couldn't move the old DLL out of the way: restart Emby and try again");
        }

        /// <summary>Deletes what an earlier update left next to the DLL.</summary>
        internal static void CleanUp(string target)
        {
            foreach (var leftover in OldNames(target).Concat(new[] { target + ".new" }))
            {
                try { if (File.Exists(leftover)) File.Delete(leftover); } catch (Exception) { }
            }
        }
    }
}
