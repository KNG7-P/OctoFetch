using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OctoFetch.Exceptions;

namespace OctoFetch.Services
{
    public class ExtractorService : IExtractorService
    {
        private readonly IAppLogger _logger;

        public ExtractorService(IAppLogger logger) => _logger = logger;

        public bool VerifySelectedParts(string[] files)
        {
            _logger.Log(LogChannel.Extractor, "");
            if (files == null || files.Length == 0) return false;

            var sortedFiles = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            _logger.Log(LogChannel.Extractor, $"🔍 Verifying {sortedFiles.Count} selected parts...");

            var isValid = true;
            for (var i = 0; i < sortedFiles.Count; i++)
            {
                var expectedExt = $".{(i + 1):D3}";
                if (!sortedFiles[i].EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Log(LogChannel.Extractor,
                        $"❌ Sequence Error: expected {expectedExt} but found {Path.GetExtension(sortedFiles[i])}");
                    isValid = false;
                }
            }

            if (isValid) _logger.Log(LogChannel.Extractor,
                $"✅ {sortedFiles.Count} sequential parts verified. Ready for native extraction.");
            return isValid;
        }

        public async Task ExtractAsync(string firstPartPath, CancellationToken cancellationToken = default)
        {
            var directory = Path.GetDirectoryName(firstPartPath)
                ?? throw new OctoFetchException("Cannot determine directory for selected part.");
            var baseName = Path.GetFileNameWithoutExtension(firstPartPath);

            var allParts = Directory.GetFiles(directory, baseName + ".*")
                .Where(f => Regex.IsMatch(f, @"\.\d{3}$"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allParts.Count == 0)
                throw new OctoFetchException("No parts matching the .NNN pattern were found in the selected directory.");

            var tempZipPath = Path.Combine(directory, baseName);
            var outDir = Path.Combine(directory, "Extracted_" + Path.GetFileNameWithoutExtension(baseName));

            _logger.Log(LogChannel.Extractor, "🔄 Merging parts natively in C#...");

            try
            {
                await using (var destStream = new FileStream(
                    tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    foreach (var part in allParts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var srcStream = new FileStream(
                            part, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                        await srcStream.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
                    }
                }

                _logger.Log(LogChannel.Extractor, "🗜️ Extracting ZIP archive...");
                Directory.CreateDirectory(outDir);

                await Task.Run(
                    () => ZipFile.ExtractToDirectory(tempZipPath, outDir, overwriteFiles: true),
                    cancellationToken).ConfigureAwait(false);

                File.Delete(tempZipPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (File.Exists(tempZipPath))
                {
                    try { File.Delete(tempZipPath); } catch { }
                }
                throw new OctoFetchException("Extraction failed: " + ex.Message, ex);
            }

            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                _logger.Log(LogChannel.Extractor, $"✅ Extraction complete! Folder: {outDir}");
                try { Process.Start("explorer.exe", outDir); }
                catch (Exception ex) { _logger.LogException(LogChannel.Extractor, "Could not open Explorer", ex); }
            }
        }
    }
}
