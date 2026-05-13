using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Services
{
    public class YouTubeResolverResult
    {
        public string DownloadUrl { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
    }

    public interface IYouTubeResolverService
    {
        Task<YouTubeResolverResult> ResolveAsync(
            string videoUrl,
            string format,
            string quality,
            Action<string>? onStatusMessage,
            CancellationToken cancellationToken);
    }

    public class YouTubeResolverService : IYouTubeResolverService
    {
        private const string ApiEndpoint = "https://hub.ytconvert.org/api/download";
        private const string HostWarmUp = "https://media.ytmp3.gg/";
        private const int PollDelayMs = 2000;
        private const int PollMaxAttempts = 150;

        private readonly IAppLogger _logger;
        private readonly Func<bool> _allowInsecureSslProvider;

        public YouTubeResolverService(IAppLogger logger, Func<bool> allowInsecureSslProvider)
        {
            _logger = logger;
            _allowInsecureSslProvider = allowInsecureSslProvider;
        }

        public async Task<YouTubeResolverResult> ResolveAsync(
            string videoUrl,
            string format,
            string quality,
            Action<string>? onStatusMessage,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new ArgumentException("videoUrl cannot be empty.", nameof(videoUrl));

            var fmt = string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "mp4";
            var isAudio = fmt == "mp3";
            string normalisedQuality;
            if (isAudio)
            {
                normalisedQuality = quality switch
                {
                    "64kbps" or "128kbps" or "192kbps" or "320kbps" => quality,
                    _ => "192kbps",
                };
            }
            else
            {
                normalisedQuality = quality switch
                {
                    "360p" or "480p" or "720p" or "1080p" => quality,
                    _ => "720p",
                };
            }

            using var handler = new HttpClientHandler();
            if (_allowInsecureSslProvider())
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            try { await http.GetAsync(HostWarmUp, cancellationToken).ConfigureAwait(false); }
            catch { }

            var payload = isAudio
                ? (object)new
                {
                    url = videoUrl,
                    os = "windows",
                    output = new { type = "audio", format = fmt },
                    audio = new { bitrate = normalisedQuality },
                }
                : (object)new
                {
                    url = videoUrl,
                    os = "windows",
                    output = new { type = "video", format = fmt, quality = normalisedQuality },
                };

            onStatusMessage?.Invoke("Requesting conversion…");
            using var resp = await http.PostAsJsonAsync(ApiEndpoint, payload, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(
                await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;

            var statusUrl = root.TryGetProperty("statusUrl", out var su) ? su.GetString() : null;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;

            if (string.IsNullOrEmpty(statusUrl))
                throw new IOException("Resolver did not return a statusUrl.");

            onStatusMessage?.Invoke("Waiting for conversion…");
            string? downloadUrl = null;
            for (var i = 0; i < PollMaxAttempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(PollDelayMs, cancellationToken).ConfigureAwait(false);

                using var pollResp = await http.GetAsync(statusUrl, cancellationToken).ConfigureAwait(false);
                if (!pollResp.IsSuccessStatusCode) continue;

                using var pollDoc = JsonDocument.Parse(
                    await pollResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var pollRoot = pollDoc.RootElement;

                var status = pollRoot.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (status == "completed")
                {
                    downloadUrl = pollRoot.TryGetProperty("downloadUrl", out var d) ? d.GetString() : null;
                    if (!string.IsNullOrEmpty(downloadUrl)) break;
                }
                else if (status == "failed")
                {
                    throw new IOException("Server failed to prepare the video.");
                }
            }

            if (string.IsNullOrEmpty(downloadUrl))
                throw new TimeoutException("YouTube conversion took too long; aborting.");

            var safeTitle = string.IsNullOrWhiteSpace(title) ? "video" : title!;
            var videoId = ExtractVideoId(videoUrl) ?? "unknown";
            var fileName = BuildFileName(safeTitle, videoId, normalisedQuality, fmt);

            return new YouTubeResolverResult
            {
                DownloadUrl = downloadUrl!,
                Title = safeTitle,
                FileName = fileName,
            };
        }

        private static string? ExtractVideoId(string url)
        {
            var m = Regex.Match(url, @"(?:v=|youtu\.be/|shorts/|embed/|live/)([a-zA-Z0-9_-]{11})");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string BuildFileName(string title, string videoId, string quality, string format)
        {
            var clean = Regex.Replace(title.Replace(' ', '_'), @"[^a-zA-Z0-9\-_()\[\]]", "_");
            clean = Regex.Replace(clean, @"_+", "_").Trim('_', '-');
            if (clean.Length > 70) clean = clean.Substring(0, 70);
            if (clean.Length == 0) clean = videoId;
            return $"{clean}_{videoId}_{quality}.{format}";
        }
    }
}
