using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace OctoFetch.Services
{
    public sealed class DpapiDataStore : IDataStore
    {
        private readonly string _folderPath;

        public DpapiDataStore(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new ArgumentException("folderPath cannot be empty.", nameof(folderPath));

            _folderPath = folderPath;
            Directory.CreateDirectory(_folderPath);
        }

        public Task StoreAsync<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key cannot be empty.", nameof(key));

            var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
            var plaintext = Encoding.UTF8.GetBytes(json);
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

            var path = GetFilePath(key, typeof(T));
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, path, overwrite: true);
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key cannot be empty.", nameof(key));

            var path = GetFilePath(key, typeof(T));
            if (!File.Exists(path))
                return Task.FromResult<T>(default!);

            try
            {
                var encrypted = File.ReadAllBytes(path);
                var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plaintext);
                var value = NewtonsoftJsonSerializer.Instance.Deserialize<T>(json);
                return Task.FromResult(value);
            }
            catch (CryptographicException)
            {
                try { File.Delete(path); } catch {  }
                return Task.FromResult<T>(default!);
            }
        }

        public Task DeleteAsync<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key cannot be empty.", nameof(key));

            var path = GetFilePath(key, typeof(T));
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch { }
            }
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            if (Directory.Exists(_folderPath))
            {
                foreach (var file in Directory.EnumerateFiles(_folderPath))
                {
                    try { File.Delete(file); }
                    catch { }
                }
            }
            return Task.CompletedTask;
        }

        public string FolderPath => _folderPath;

        private string GetFilePath(string key, Type type)
            => Path.Combine(_folderPath, $"{type.FullName}-{key}");
    }
}
