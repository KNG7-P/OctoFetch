using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Helpers
{
    public static class CurlLocalDownloader
    {
        public static string GetCurlPath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore", "curl.exe");

        public static async Task DownloadAsync(
            string url,
            string destinationPath,
            bool allowInsecureSsl,
            Action<long, long>? onProgress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("url cannot be empty.", nameof(url));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("destinationPath cannot be empty.", nameof(destinationPath));

            var curlPath = GetCurlPath();
            if (!File.Exists(curlPath))
                throw new FileNotFoundException("curl.exe not found in GitCore/.", curlPath);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            var argList = new List<string>
            {
                "-L",
                "--fail",
                "--silent",
                "--show-error",
                "--retry", "3",
                "--retry-delay", "2",
                "--connect-timeout", "30",
                "-o", QuoteArg(destinationPath),
            };
            if (allowInsecureSsl) argList.Add("-k");
            argList.Add(QuoteArg(url));

            var psi = new ProcessStartInfo
            {
                FileName = curlPath,
                Arguments = string.Join(" ", argList),
                WorkingDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GitCore"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderrBuffer = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (stderrBuffer) stderrBuffer.AppendLine(e.Data);
            };
            process.OutputDataReceived += (_, _) => { };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start curl.exe");
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var killReg = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch { }
            });

            long totalSize = 0;
            var lastReport = DateTime.MinValue;
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds >= 400)
                {
                    lastReport = now;
                    long currentBytes = 0;
                    try { if (File.Exists(destinationPath)) currentBytes = new FileInfo(destinationPath).Length; }
                    catch { }
                    onProgress?.Invoke(currentBytes, totalSize);
                }
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            }

            if (process.ExitCode != 0)
            {
                string stderr;
                lock (stderrBuffer) stderr = stderrBuffer.ToString().Trim();
                if (stderr.Length > 400) stderr = stderr[..400] + "…";
                throw new IOException(string.IsNullOrEmpty(stderr)
                    ? $"curl failed for {url} (exit {process.ExitCode})"
                    : $"curl failed for {url}: {stderr}");
            }

            if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
                throw new IOException($"Downloaded file is empty: {destinationPath}");

            var finalSize = new FileInfo(destinationPath).Length;
            onProgress?.Invoke(finalSize, finalSize);
        }

        private static string QuoteArg(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }
    }
}
