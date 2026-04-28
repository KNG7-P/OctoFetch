using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Diagnostics;

namespace OctoFetch.Services
{
    public class ExtractorService
    {
        private readonly Action<string> _logger;

        public ExtractorService(Action<string> logger)
        {
            _logger = logger;
        }

        public bool VerifySelectedParts(string[] files)
        {
            _logger("");
            if (files == null || files.Length == 0) return false;

            var sortedFiles = files.OrderBy(f => f).ToList();

            _logger($"🔍 Verifying {sortedFiles.Count} selected parts...");
            bool isValid = true;
            for (int i = 0; i < sortedFiles.Count; i++)
            {
                string expectedExt = $".{(i + 1):D3}"; 
                if (!sortedFiles[i].EndsWith(expectedExt))
                {
                    _logger($"❌ Sequence Error: Expected {expectedExt} but found {Path.GetExtension(sortedFiles[i])}");
                    isValid = false;
                }
            }

            if (isValid) _logger($"✅ {sortedFiles.Count} sequential parts verified. Ready for native extraction.");
            return isValid;
        }

        public async Task ExtractAsync(string firstPartPath)
        {
            string directory = Path.GetDirectoryName(firstPartPath);
            string baseName = Path.GetFileNameWithoutExtension(firstPartPath); 

            var allParts = Directory.GetFiles(directory, baseName + ".*")
                .Where(f => Regex.IsMatch(f, @"\.\d{3}$"))
                .OrderBy(f => f).ToList();

            string tempZipPath = Path.Combine(directory, baseName);
            string outDir = Path.Combine(directory, "Extracted_" + Path.GetFileNameWithoutExtension(baseName));

            _logger("🔄 Merging parts natively in C#...");

            await Task.Run(() =>
            {
                try
                {
                    using (var destStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write))
                    {
                        foreach (var part in allParts)
                        {
                            using (var srcStream = new FileStream(part, FileMode.Open, FileAccess.Read))
                            {
                                srcStream.CopyTo(destStream);
                            }
                        }
                    }

                    _logger("🗜️ Extracting ZIP archive...");
                    if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

                    ZipFile.ExtractToDirectory(tempZipPath, outDir, overwriteFiles: true);

                    File.Delete(tempZipPath);
                }
                catch (Exception ex)
                {
                    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                    throw new Exception("Extraction failed: " + ex.Message);
                }
            });

            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                _logger($"✅ Extraction complete! Folder: {outDir}");
                Process.Start("explorer.exe", outDir);
            }
        }
    }
}