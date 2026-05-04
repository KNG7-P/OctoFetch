using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Services
{
    public class CurlHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _curlPath;
        private readonly string? _caCertPath;
        private readonly IAppLogger _logger;
        private static int _sslWarningLogged;

        public bool AllowInsecureSsl { get; set; }

        public CurlHttpMessageHandler(IAppLogger logger, bool allowInsecureSsl = false)
        {
            _logger = logger;

            var gitCoreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore");
            _curlPath = Path.Combine(gitCoreDir, "curl.exe");

            foreach (var candidate in new[] { "cacert.pem", "curl-ca-bundle.crt", "ca-bundle.crt" })
            {
                var path = Path.Combine(gitCoreDir, candidate);
                if (File.Exists(path)) { _caCertPath = path; break; }
            }

            AllowInsecureSsl = allowInsecureSsl;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!File.Exists(_curlPath))
                throw new FileNotFoundException("curl.exe not found in GitCore/.", _curlPath);
            if (request.RequestUri == null)
                throw new InvalidOperationException("Request URI is null.");

            var tempDir = Path.Combine(Path.GetTempPath(), "OctoFetch");
            Directory.CreateDirectory(tempDir);

            string? tempBodyFile = null;
            var tempHeaderFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".tmp");
            var tempOutFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                var args = new StringBuilder();
                args.Append("--retry 3 --retry-delay 2 --connect-timeout 20 -s ");

                if (AllowInsecureSsl)
                {
                    args.Append("-k ");
                }
                else if (_caCertPath != null)
                {
                    args.Append($"--cacert \"{_caCertPath}\" ");
                }
                else
                {
                    if (Interlocked.CompareExchange(ref _sslWarningLogged, 1, 0) == 0)
                    {
                        _logger.Log(LogChannel.Settings,
                            "⚠️ No 'cacert.pem' found in GitCore/. Falling back to insecure SSL (-k). " +
                            "For production use, ship a cacert.pem next to curl.exe to enable TLS verification.");
                    }
                    args.Append("-k ");
                }

                args.Append($"-D \"{tempHeaderFile}\" ");
                args.Append($"-o \"{tempOutFile}\" ");
                args.Append($"-X {request.Method.Method} ");

                foreach (var header in request.Headers)
                    args.Append($"-H \"{header.Key}: {string.Join(", ", header.Value)}\" ");

                if (request.Content != null)
                {
                    foreach (var header in request.Content.Headers)
                        args.Append($"-H \"{header.Key}: {string.Join(", ", header.Value)}\" ");

                    var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (bodyBytes.Length > 0)
                    {
                        tempBodyFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".tmp");
                        await File.WriteAllBytesAsync(tempBodyFile, bodyBytes, cancellationToken).ConfigureAwait(false);
                        args.Append($"-d @\"{tempBodyFile}\" ");
                    }
                }

                args.Append($"\"{request.RequestUri}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = _curlPath,
                    WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore"),
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(psi)
                                    ?? throw new InvalidOperationException("Failed to launch curl.exe");

                try
                {
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                    }
                    throw;
                }

                var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

                var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    RequestMessage = request,
                };

                // Body
                if (File.Exists(tempOutFile) && new FileInfo(tempOutFile).Length > 0)
                {
                    var bytes = await File.ReadAllBytesAsync(tempOutFile, cancellationToken).ConfigureAwait(false);
                    response.Content = new ByteArrayContent(bytes);
                }
                else if (process.ExitCode != 0)
                {
                    var msg = string.IsNullOrWhiteSpace(stderr)
                        ? $"curl exited with code {process.ExitCode}"
                        : $"curl exited with code {process.ExitCode}: {stderr.Trim()}";
                    response.Content = new StringContent(msg, Encoding.UTF8, "text/plain");
                }
                else
                {
                    response.Content = new ByteArrayContent(Array.Empty<byte>());
                }

                if (File.Exists(tempHeaderFile))
                {
                    var headerLines = await File.ReadAllLinesAsync(tempHeaderFile, cancellationToken).ConfigureAwait(false);
                    if (headerLines.Length > 0)
                    {
                        var statusLine = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (statusLine.Length >= 2 && int.TryParse(statusLine[1], out var code))
                            response.StatusCode = (HttpStatusCode)code;

                        for (var i = 1; i < headerLines.Length; i++)
                        {
                            var line = headerLines[i];
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var sep = line.IndexOf(':');
                            if (sep <= 0) continue;

                            var key = line[..sep].Trim();
                            var val = line[(sep + 1)..].Trim();

                            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                                response.Content.Headers.TryAddWithoutValidation(key, val);
                            else
                                response.Headers.TryAddWithoutValidation(key, val);
                        }
                    }
                }

                if (!response.Content.Headers.Contains("Content-Type"))
                {
                    response.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", "application/json; charset=utf-8");
                }

                return response;
            }
            finally
            {
                TryDelete(tempBodyFile);
                TryDelete(tempHeaderFile);
                TryDelete(tempOutFile);
            }
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
