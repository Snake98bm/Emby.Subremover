using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Subremover.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Session;

namespace Emby.Subremover
{
    /// <summary>
    /// Offers "OCR of the built-in subtitles" in Emby's subtitle search, when "Offer OCR in Emby's subtitle search" is on.
    /// Picking it reads the picture track with OCR and hands Emby the text, which Emby saves like any downloaded subtitle.
    /// Players showing the video get a message when the OCR starts, finishes or fails, since it takes minutes.
    /// </summary>
    public class OcrSubtitleProvider : ISubtitleProvider
    {
        // One OCR at a time: each one reads the whole video and keeps a CPU core busy for minutes.
        private static readonly SemaphoreSlim OneAtATime = new SemaphoreSlim(1, 1);

        private readonly FfmpegRunner _runner;
        private readonly Tessdata _tessdata;
        private readonly IJsonSerializer _json;
        private readonly ILogger _log;
        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;

        public OcrSubtitleProvider(IFfmpegManager ffmpegManager, IApplicationPaths appPaths, IJsonSerializer json, ILogManager logManager,
            ISessionManager sessions, ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _runner = FfmpegRunner.FromEmby(ffmpegManager);
            _tessdata = new Tessdata(Path.Combine(appPaths.DataPath, "subremover"));
            _json = json;
            _log = logManager.GetLogger("SubRemover");
            _sessions = sessions;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        public string Name => "SubRemover OCR";

        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Movie, VideoContentType.Episode };

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var options = Plugin.Instance?.Options;
            if (options == null || !options.OfferOcrInSubtitleSearch || string.IsNullOrEmpty(request.MediaPath) || !File.Exists(request.MediaPath))
                return Array.Empty<RemoteSubtitleInfo>();

            var probe = await Probe(request.MediaPath, cancellationToken).ConfigureAwait(false);
            if (probe == null) return Array.Empty<RemoteSubtitleInfo>();

            return OcrOffer.For(probe, request.MediaPath, request.Language).Select(o => new RemoteSubtitleInfo
            {
                Id = o.ToId(),
                ProviderName = Name,
                Name = o.Description,
                Comment = "Takes a few minutes: the picture subtitles are read with OCR, which can misread words.",
                Format = "srt",
                Language = request.Language,
                ThreeLetterISOLanguageName = o.Language,
                IsForced = o.IsForced,
                IsHearingImpaired = o.IsHearingImpaired
            }).ToList();
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            var offer = OcrOffer.FromId(id);
            var probe = await Probe(offer.VideoPath, cancellationToken).ConfigureAwait(false);
            var stream = probe?.streams.FirstOrDefault(s => s.index == offer.StreamIndex && s.IsSubtitle)
                         ?? throw new InvalidOperationException($"Subtitle track #{offer.StreamIndex} is no longer in {offer.VideoPath}");

            // An OCR file a SubRemover run already made for this track is used as it is.
            var made = SubtitlePlanner.OcrFilePath(offer.VideoPath, stream);
            if (File.Exists(made))
            {
                _log.Info($"SubRemover: subtitle search - using the existing OCR file {made}");
                return Response(offer, File.ReadAllText(made));
            }

            // The OCR doesn't stop when the page stops waiting (dialog closed, request timed out): it finishes and the
            // text is saved next to the video instead, so the minutes of work aren't lost.
            var work = Task.Run(() => Ocr(offer, stream));
            var gaveUp = Task.Delay(Timeout.Infinite, cancellationToken);
            if (await Task.WhenAny(work, gaveUp).ConfigureAwait(false) == gaveUp)
            {
                _ = work.ContinueWith(t => SaveForLater(offer, made, t), TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }

            var text = await work.ConfigureAwait(false);
            Notify(offer.VideoPath, "The OCR subtitles are ready. Open the subtitle menu and pick the new English one (reopen the menu if it isn't there yet).");
            return Response(offer, text);
        }

        private async Task<string> Ocr(OcrOffer offer, ProbeStream stream)
        {
            if (OneAtATime.CurrentCount == 0) Notify(offer.VideoPath, "Another OCR is running; this one starts when it's done.");
            await OneAtATime.WaitAsync().ConfigureAwait(false);
            var temp = Path.Combine(Path.GetTempPath(), "subremover-ocr-" + Guid.NewGuid().ToString("N") + ".srt");
            try
            {
                _log.Info($"SubRemover: subtitle search - OCR of {stream.Describe()} in {offer.VideoPath}");
                Notify(offer.VideoPath, "Reading the subtitles with OCR. This takes a few minutes; keep watching, they'll be in the subtitle menu when ready.");
                var job = new OcrJob { Stream = stream, OutputPath = temp, TesseractLanguage = offer.TesseractLanguage };
                var problem = await SubtitleOcr.Run(_runner, _tessdata, offer.VideoPath, job, CancellationToken.None).ConfigureAwait(false);
                if (problem != null)
                {
                    _log.Warn($"SubRemover: subtitle search - OCR of {offer.VideoPath} failed: {problem}");
                    Notify(offer.VideoPath, "The OCR didn't work: " + problem);
                    throw new InvalidOperationException("OCR failed: " + problem);
                }
                return File.ReadAllText(temp);
            }
            finally
            {
                OneAtATime.Release();
                BackupManager.TryDelete(temp);
            }
        }

        // The page stopped waiting: keep the text as the OCR file a SubRemover run would make, and let Emby find it.
        private void SaveForLater(OcrOffer offer, string path, Task<string> work)
        {
            if (work.Status != TaskStatus.RanToCompletion) return;
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, work.Result);
                _log.Info($"SubRemover: subtitle search - the page stopped waiting, so the OCR text was saved as {path}");
                var item = _libraryManager.FindByPath(offer.VideoPath, false);
                if (item != null)
                {
                    _providerManager.QueueRefresh(item.InternalId, new MetadataRefreshOptions(_fileSystem)
                    {
                        MetadataRefreshMode = MetadataRefreshMode.Default,
                        ReplaceAllMetadata = false
                    }, RefreshPriority.High);
                }
                Notify(offer.VideoPath, "The OCR subtitles are ready and saved next to the video. They'll show in the subtitle menu after Emby refreshes it (stop and play again if they don't).");
            }
            catch (Exception ex)
            {
                _log.ErrorException("SubRemover: subtitle search - couldn't save the OCR text for {0}", ex, offer.VideoPath);
            }
        }

        // Shows a message on every player that's playing the video. A message that can't be shown never stops the OCR.
        private void Notify(string videoPath, string text)
        {
            try
            {
                var players = _sessions.Sessions
                    .Where(s => s.IsActive && string.Equals(s.FullNowPlayingItem?.Path, videoPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var player in players)
                {
                    _ = _sessions.SendMessageCommand(null, player.Id, new MessageCommand { Header = "SubRemover", Text = text, TimeoutMs = 10000 }, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"SubRemover: couldn't show a message on the player: {ex.Message}");
            }
        }

        private static SubtitleResponse Response(OcrOffer offer, string text) => new SubtitleResponse
        {
            Format = "srt",
            Language = offer.Language,
            IsForced = offer.IsForced,
            IsHearingImpaired = offer.IsHearingImpaired,
            Stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))
        };

        private async Task<ProbeResult> Probe(string path, CancellationToken ct)
        {
            var result = await _runner.RunFfprobe(FfmpegArgs.Probe(path), ct).ConfigureAwait(false);
            if (!result.Success)
            {
                _log.Warn($"SubRemover: subtitle search - ffprobe failed for {path} (exit {result.ExitCode})");
                return null;
            }
            return _json.DeserializeFromString<ProbeResult>(result.StdOut);
        }
    }
}
