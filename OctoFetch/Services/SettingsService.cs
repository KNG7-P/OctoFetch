using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _appFolder;
        private readonly string _encryptedPath;
        private readonly string _legacyPlainPath;
        private readonly IAppLogger _logger;

        public SettingsService(IAppLogger logger)
        {
            _logger = logger;
            _appFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OctoFetch");
            Directory.CreateDirectory(_appFolder);

            _encryptedPath = Path.Combine(_appFolder, "settings.dat");
            _legacyPlainPath = Path.Combine(_appFolder, "settings.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_encryptedPath) && File.Exists(_legacyPlainPath))
            {
                try
                {
                    var legacy = File.ReadAllText(_legacyPlainPath);
                    var migrated = JsonConvert.DeserializeObject<AppSettings>(legacy) ?? new AppSettings();
                    MigrateLegacyDefaults(migrated);
                    Save(migrated);
                    File.Delete(_legacyPlainPath);
                    _logger.Log(LogChannel.Settings, "🔐 Migrated plain-text settings to encrypted store and removed the legacy file.");
                    return migrated;
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Settings, "Failed to migrate legacy settings", ex);
                }
            }

            if (!File.Exists(_encryptedPath))
                return new AppSettings();

            try
            {
                var encrypted = File.ReadAllBytes(_encryptedPath);
                var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(decrypted);
                var loaded = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                MigrateLegacyDefaults(loaded);
                return loaded;
            }
            catch (CryptographicException ex)
            {
                _logger.LogException(LogChannel.Settings, "Settings file is corrupt or was encrypted by another user. Resetting", ex);
                return new AppSettings();
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Failed to load settings", ex);
                return new AppSettings();
            }
        }

        private static void MigrateLegacyDefaults(AppSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.ChunkSize) ||
                s.ChunkSize.Equals("90M", StringComparison.OrdinalIgnoreCase) ||
                s.ChunkSize.Equals("100M", StringComparison.OrdinalIgnoreCase))
            {
                s.ChunkSize = "45M";
            }

            if (s.PollIntervalSeconds >= 8) s.PollIntervalSeconds = 3;

            s.YouTubeApiKey = string.Empty;
            s.YouTubeApiKeys ??= new System.Collections.Generic.List<string>();
        }

        public void Save(AppSettings settings)
        {
            try
            {
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                var bytes = Encoding.UTF8.GetBytes(json);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                var tmp = _encryptedPath + ".tmp";
                File.WriteAllBytes(tmp, encrypted);
                File.Move(tmp, _encryptedPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Failed to save settings", ex);
            }
        }
    }
}
