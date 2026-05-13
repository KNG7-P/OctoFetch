using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using OctoFetch.Models;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using DrivePermission = Google.Apis.Drive.v3.Data.Permission;

namespace OctoFetch.Services
{
    public class GoogleDriveService : IGoogleDriveService
    {
       
        private static readonly string ClientId =
            string.Concat("905954805343-0unlh", "fbod3fofa8n7285d280", "hv1gpa6i") +
            ".apps." + "googleusercontent" + ".com";
        private static readonly string ClientSecret =
            string.Concat("GOC", "SPX-", "Bwwqvj--", "akRjXZh", "ToxkQQ0", "NNUT1L");

        private static readonly string[] Scopes = new[] { DriveService.Scope.DriveFile };

        private const string RootFolderName = "OctoFetch";

        private const string ApplicationName = "OctoFetch";

        private readonly IAppLogger _logger;
        private readonly Func<bool> _allowInsecureSslProvider;
        private readonly IMitmService? _mitm;
        private readonly Action<string?, string?> _onAccountInfo;

        private readonly string _tokenFolder;
        private readonly DpapiDataStore _dataStore;

        private UserCredential? _credential;
        private DriveService? _drive;

        private readonly Dictionary<string, string> _folderIdCache = new(StringComparer.OrdinalIgnoreCase);

        private string? _accountEmail;
        private string? _accountDisplayName;

        public bool IsConnected => _credential != null && _drive != null;
        public string? AccountEmail => _accountEmail;
        public string? AccountDisplayName => _accountDisplayName;

        public event Action? ConnectionChanged;

        public DriveActionsCredentials? GetActionsCredentials()
        {
            var token = _credential?.Token;
            if (token == null) return null;
            if (string.IsNullOrEmpty(token.RefreshToken)) return null;
            return new DriveActionsCredentials(token.RefreshToken, ClientId, ClientSecret);
        }

        public GoogleDriveService(
            IAppLogger logger,
            Func<bool> allowInsecureSslProvider,
            Action<string?, string?> onAccountInfo,
            IMitmService? mitm = null)
        {
            _logger = logger;
            _allowInsecureSslProvider = allowInsecureSslProvider;
            _mitm = mitm;
            _onAccountInfo = onAccountInfo;

            _tokenFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OctoFetch", "Drive");
            Directory.CreateDirectory(_tokenFolder);
            _dataStore = new DpapiDataStore(_tokenFolder);
        }

        public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var flow = CreateFlow();
                var token = await flow.LoadTokenAsync("user", cancellationToken).ConfigureAwait(false);
                if (token == null || string.IsNullOrEmpty(token.RefreshToken))
                    return false;

                _credential = new UserCredential(flow, "user", token);

                BuildDriveClient();
                await PopulateAccountInfoAsync(cancellationToken).ConfigureAwait(false);
                RaiseConnectionChanged();
                _logger.Log(LogChannel.Settings, $"☁️ Google Drive: signed in as {_accountEmail ?? "(unknown)"}");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Failed to restore Drive token", ex);
                return false;
            }
        }

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            IDisposable mitmLease = _mitm != null
                ? await _mitm.AcquireAsync("drive-oauth", cancellationToken).ConfigureAwait(false)
                : (IDisposable)new MitmNoOpScope();
            IDisposable sysProxyScope = (_mitm != null && _mitm.IsRunning)
                ? _mitm.EnableSystemProxyScope()
                : (IDisposable)new MitmNoOpScope();

            try
            {
                var receiver = new LocalServerCodeReceiver(DriveCallbackPageHtml);

                var flow = CreateFlow();
                var app = new AuthorizationCodeInstalledApp(flow, receiver);

                _credential = await app.AuthorizeAsync("user", cancellationToken)
                    .ConfigureAwait(false);

                BuildDriveClient();
                await PopulateAccountInfoAsync(cancellationToken).ConfigureAwait(false);
                RaiseConnectionChanged();
                _logger.Log(LogChannel.Settings, $"☁️ Google Drive connected: {_accountEmail ?? "(unknown)"}");
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.Log(LogChannel.Settings, "Drive authorization cancelled by user.");
                return false;
            }
            catch (TokenResponseException ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive authorization rejected", ex);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive authorization failed", ex);
                return false;
            }
            finally
            {
                sysProxyScope.Dispose();
                mitmLease.Dispose();
            }
        }

        private sealed class MitmNoOpScope : IDisposable
        {
            public void Dispose() { }
        }

        private const string DriveCallbackPageHtml = @"<!doctype html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <title>OctoFetch — Drive connected</title>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <style>
    *{box-sizing:border-box}
    html,body{margin:0;height:100%}
    body{
      font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif;
      background:radial-gradient(circle at top,#1A1A26 0%,#0B0B12 70%);
      color:#E8E8F2;
      display:flex;align-items:center;justify-content:center;
      -webkit-font-smoothing:antialiased;
    }
    .card{
      width:min(440px,92vw);
      padding:36px 32px;
      background:rgba(26,26,38,.92);
      border:1px solid #2A2A3C;
      border-radius:16px;
      box-shadow:0 20px 60px rgba(0,0,0,.45);
      text-align:center;
    }
    .badge{
      width:64px;height:64px;margin:0 auto 18px;
      border-radius:50%;
      background:linear-gradient(135deg,#10B981,#059669);
      display:flex;align-items:center;justify-content:center;
      box-shadow:0 8px 24px rgba(16,185,129,.35);
    }
    .badge svg{width:32px;height:32px;color:#fff}
    h1{margin:0 0 8px;font-size:22px;font-weight:600;letter-spacing:.2px}
    p{margin:6px 0;color:#A0A0B0;font-size:14.5px;line-height:1.55}
    .accent{color:#3B82F6;font-weight:600}
    .hint{
      margin-top:22px;padding:12px 14px;
      background:#11111A;border:1px solid #1F1F2C;border-radius:10px;
      font-size:13px;color:#9090A4;
    }
    #cd{font-variant-numeric:tabular-nums;color:#E8E8F2;font-weight:600}
  </style>
</head>
<body>
  <main class=""card"" role=""status"" aria-live=""polite"">
    <div class=""badge"" aria-hidden=""true"">
      <svg viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""3"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M5 12.5l4.5 4.5L19 7""/></svg>
    </div>
    <h1>Google Drive connected</h1>
    <p>You can safely <span class=""accent"">return to OctoFetch</span> — your Drive is now linked.</p>
    <div class=""hint"">This tab will try to close in <span id=""cd"">3</span>s. If it stays open, you can close it manually.</div>
  </main>
  <script>
    (function(){
      var n=3, el=document.getElementById('cd');
      var t=setInterval(function(){
        n--;
        if(el) el.textContent=String(Math.max(0,n));
        if(n<=0){
          clearInterval(t);
          try{ window.close(); }catch(e){}
        }
      },1000);
    })();
  </script>
</body>
</html>";

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (_credential != null)
                {
                    try { await _credential.RevokeTokenAsync(cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _logger.LogException(LogChannel.Settings, "Drive token revoke failed (continuing)", ex);
                    }
                }

                await _dataStore.ClearAsync().ConfigureAwait(false);
            }
            finally
            {
                _credential = null;
                _drive?.Dispose();
                _drive = null;
                _accountEmail = null;
                _accountDisplayName = null;
                _folderIdCache.Clear();
                _onAccountInfo(null, null);
                RaiseConnectionChanged();
                _logger.Log(LogChannel.Settings, "☁️ Google Drive disconnected.");
            }
        }

        public async Task<GoogleDriveUploadResult> UploadFileAsync(
            string localPath,
            string category,
            string? displayFileName,
            Action<long, long>? onProgress,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || _drive == null)
                throw new InvalidOperationException("Google Drive is not connected.");
            if (!File.Exists(localPath))
                throw new FileNotFoundException("Local file to upload was not found.", localPath);

            var info = new FileInfo(localPath);
            var fileName = string.IsNullOrWhiteSpace(displayFileName)
                ? Path.GetFileName(localPath)
                : displayFileName!;

            var folderId = await EnsureCategoryFolderAsync(category, cancellationToken).ConfigureAwait(false);

            var metadata = new DriveFile
            {
                Name = fileName,
                Parents = new List<string> { folderId },
            };

            using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var request = _drive.Files.Create(metadata, stream, GuessContentType(fileName));
            request.Fields = "id,name,size,webViewLink,webContentLink";
            request.ChunkSize = 1024 * 1024;

            var totalBytes = info.Length;
            request.ProgressChanged += progress =>
            {
                if (progress.Status == UploadStatus.Failed)
                {
                    _logger.LogException(LogChannel.Downloader,
                        $"Drive upload failed for '{fileName}'",
                        progress.Exception ?? new Exception("Unknown error"));
                }
                onProgress?.Invoke(progress.BytesSent, totalBytes);
            };

            var status = await request.UploadAsync(cancellationToken).ConfigureAwait(false);
            if (status.Status != UploadStatus.Completed)
            {
                throw status.Exception ?? new IOException($"Drive upload ended in state {status.Status}.");
            }

            var uploaded = request.ResponseBody
                ?? throw new IOException("Drive returned no metadata for the uploaded file.");

            try
            {
                var perm = new DrivePermission { Type = "anyone", Role = "reader" };
                var permReq = _drive.Permissions.Create(perm, uploaded.Id);
                permReq.Fields = "id";
                await permReq.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Downloader, "Drive share-permission set failed", ex);
            }

            var shareUrl = !string.IsNullOrEmpty(uploaded.WebViewLink)
                ? uploaded.WebViewLink
                : $"https://drive.google.com/file/d/{uploaded.Id}/view";

            return new GoogleDriveUploadResult
            {
                FileId = uploaded.Id,
                FileName = uploaded.Name ?? fileName,
                Size = uploaded.Size ?? totalBytes,
                ShareUrl = shareUrl,
            };
        }

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        private GoogleAuthorizationCodeFlow CreateFlow()
        {
            return new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = ClientId, ClientSecret = ClientSecret },
                Scopes = Scopes,
                DataStore = _dataStore,
                HttpClientFactory = CreateHttpClientFactory(),
            });
        }

        private void BuildDriveClient()
        {
            _drive?.Dispose();
            _drive = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = _credential,
                ApplicationName = ApplicationName,
                HttpClientFactory = CreateHttpClientFactory(),
            });
        }
        private IHttpClientFactory CreateHttpClientFactory()
        {
            return new OctoFetchDriveHttpClientFactory(_allowInsecureSslProvider, _mitm);
        }

        // -------------------------------------------------------------------
        // Listing previously-uploaded files (for the File Manager Drive tab)
        // -------------------------------------------------------------------
        public async Task<IReadOnlyList<DriveFileItem>> ListUploadedFilesAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected) return Array.Empty<DriveFileItem>();

            try
            {
                var rootId = await FindFolderIdAsync(RootFolderName, parentId: null, cancellationToken)
                    .ConfigureAwait(false);
                if (rootId == null) return Array.Empty<DriveFileItem>();

                _folderIdCache[$"<root>/{RootFolderName}"] = rootId;

                var categories = new List<(string Id, string Name)>();
                string? pageToken = null;
                do
                {
                    var req = _drive!.Files.List();
                    req.Q = $"mimeType = 'application/vnd.google-apps.folder' and trashed = false and '{rootId}' in parents";
                    req.Fields = "nextPageToken, files(id, name)";
                    req.PageSize = 100;
                    req.Spaces = "drive";
                    req.PageToken = pageToken;

                    var page = await req.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    if (page.Files != null)
                    {
                        foreach (var f in page.Files)
                        {
                            if (!string.IsNullOrEmpty(f.Id) && !string.IsNullOrEmpty(f.Name))
                                categories.Add((f.Id, f.Name));
                        }
                    }
                    pageToken = page.NextPageToken;
                } while (!string.IsNullOrEmpty(pageToken));

                var results = new List<DriveFileItem>();

                await ListFilesInFolderAsync(rootId, "Other", results, cancellationToken).ConfigureAwait(false);

                foreach (var (catId, catName) in categories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ListFilesInFolderAsync(catId, catName, results, cancellationToken).ConfigureAwait(false);
                }

                results.Sort((a, b) =>
                    Nullable.Compare(b.ModifiedAt, a.ModifiedAt));

                return results;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive: failed to list uploaded files", ex);
                return Array.Empty<DriveFileItem>();
            }
        }

        private async Task<string?> FindFolderIdAsync(string name, string? parentId, CancellationToken ct)
        {
            var parentClause = parentId == null ? "'root' in parents" : $"'{parentId}' in parents";
            var req = _drive!.Files.List();
            req.Q = $"mimeType = 'application/vnd.google-apps.folder' and trashed = false " +
                    $"and name = {EscapeForQuery(name)} and {parentClause}";
            req.Fields = "files(id,name)";
            req.PageSize = 5;
            req.Spaces = "drive";

            var found = await req.ExecuteAsync(ct).ConfigureAwait(false);
            if (found.Files != null && found.Files.Count > 0)
                return found.Files[0].Id;
            return null;
        }

        private async Task ListFilesInFolderAsync(
            string folderId,
            string categoryName,
            List<DriveFileItem> sink,
            CancellationToken ct)
        {
            string? pageToken = null;
            do
            {
                var req = _drive!.Files.List();
                req.Q = $"'{folderId}' in parents and trashed = false " +
                        $"and mimeType != 'application/vnd.google-apps.folder'";
                req.Fields = "nextPageToken, files(id, name, size, modifiedTime, webViewLink, mimeType)";
                req.PageSize = 200;
                req.Spaces = "drive";
                req.PageToken = pageToken;
                req.OrderBy = "modifiedTime desc";

                var page = await req.ExecuteAsync(ct).ConfigureAwait(false);
                if (page.Files != null)
                {
                    foreach (var f in page.Files)
                    {
                        if (string.IsNullOrEmpty(f.Id) || string.IsNullOrEmpty(f.Name)) continue;

                        var shareUrl = !string.IsNullOrEmpty(f.WebViewLink)
                            ? f.WebViewLink
                            : $"https://drive.google.com/file/d/{f.Id}/view";

                        DateTimeOffset? modified = null;
                        if (!string.IsNullOrEmpty(f.ModifiedTimeRaw)
                            && DateTimeOffset.TryParse(f.ModifiedTimeRaw, out var parsed))
                        {
                            modified = parsed;
                        }

                        sink.Add(new DriveFileItem
                        {
                            FileId = f.Id,
                            Name = f.Name,
                            Category = string.IsNullOrWhiteSpace(categoryName) ? "Other" : categoryName,
                            ShareUrl = shareUrl,
                            SizeBytes = f.Size ?? 0,
                            ModifiedAt = modified,
                            MimeType = f.MimeType,
                        });
                    }
                }
                pageToken = page.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }

        public async Task DownloadFileAsync(
            string fileId,
            string destinationPath,
            Action<long, long>? onProgress,
            long knownSize = 0,
            long resumeFromByte = 0,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || _drive == null)
                throw new InvalidOperationException("Google Drive is not connected.");
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("File id is required.", nameof(fileId));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Destination path is required.", nameof(destinationPath));
            if (resumeFromByte < 0)
                throw new ArgumentOutOfRangeException(nameof(resumeFromByte));

            long totalBytes = knownSize;
            if (totalBytes <= 0)
            {
                try
                {
                    var meta = _drive.Files.Get(fileId);
                    meta.Fields = "size";
                    var info = await meta.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    totalBytes = info.Size ?? 0;
                }
                catch (Exception ex)
                {
                    _logger.LogException(LogChannel.Downloader, "Drive metadata probe failed (continuing)", ex);
                }
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var partPath = destinationPath + ".part";

            if (resumeFromByte > 0)
            {
                if (!File.Exists(partPath))
                {
                    resumeFromByte = 0;
                }
                else
                {
                    var existing = new FileInfo(partPath).Length;
                    if (existing < resumeFromByte) resumeFromByte = existing;
                }
            }

            try
            {
                var request = _drive.Files.Get(fileId);
                request.MediaDownloader.ChunkSize = 8 * 1024 * 1024; 
                if (resumeFromByte > 0
                    && request.MediaDownloader is Google.Apis.Download.MediaDownloader concrete)
                {
                    concrete.Range =
                        new System.Net.Http.Headers.RangeHeaderValue(resumeFromByte, null);
                }

                var byteOffset = resumeFromByte;
                request.MediaDownloader.ProgressChanged += progress =>
                {
                    var isCancellation = progress.Exception is OperationCanceledException
                                      || cancellationToken.IsCancellationRequested;
                    if (progress.Status == Google.Apis.Download.DownloadStatus.Failed && !isCancellation)
                    {
                        _logger.LogException(LogChannel.Downloader,
                            $"Drive download failed for fileId={fileId}",
                            progress.Exception ?? new Exception("Unknown error"));
                    }
                    onProgress?.Invoke(byteOffset + progress.BytesDownloaded, totalBytes);
                };

                var fileMode = resumeFromByte > 0 ? FileMode.Append : FileMode.Create;
                using (var fs = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.None))
                {
                    var status = await request.DownloadAsync(fs, cancellationToken).ConfigureAwait(false);
                    if (status.Status != Google.Apis.Download.DownloadStatus.Completed)
                        throw status.Exception ?? new IOException($"Drive download ended in state {status.Status}.");
                }

                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(partPath, destinationPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* swallow */ }
                throw;
            }
        }

        public async Task<string?> RenameFileAsync(
            string fileId,
            string newName,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || _drive == null)
                throw new InvalidOperationException("Google Drive is not connected.");
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("File id is required.", nameof(fileId));
            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New name is required.", nameof(newName));

            newName = newName.Trim();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = new DriveFile { Name = newName };
                var req = _drive.Files.Update(body, fileId);
                req.Fields = "id,name";
                var updated = await req.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                var settled = updated?.Name ?? newName;
                _logger.Log(LogChannel.Settings, $"\u270F\uFE0F Drive: renamed file to {settled}");
                return settled;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Drive rename failed (fileId={fileId})", ex);
                return null;
            }
        }

        public async Task<bool> DeleteFileAsync(
            string fileId,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected || _drive == null)
                throw new InvalidOperationException("Google Drive is not connected.");
            if (string.IsNullOrWhiteSpace(fileId))
                throw new ArgumentException("File id is required.", nameof(fileId));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var req = _drive.Files.Delete(fileId);
                await req.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                _logger.Log(LogChannel.Settings, $"\uD83D\uDDD1\uFE0F Drive: deleted file {fileId}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, $"Drive delete failed (fileId={fileId})", ex);
                return false;
            }
        }

        private async Task<string> EnsureCategoryFolderAsync(string category, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(category)) category = "Other";

            var rootId = await EnsureFolderAsync(RootFolderName, parentId: null, ct).ConfigureAwait(false);
            var catKey = $"{rootId}/{category}";
            if (_folderIdCache.TryGetValue(catKey, out var cachedCat))
                return cachedCat;

            var catId = await EnsureFolderAsync(category, rootId, ct).ConfigureAwait(false);
            _folderIdCache[catKey] = catId;
            return catId;
        }

        private async Task<string> EnsureFolderAsync(string name, string? parentId, CancellationToken ct)
        {
            var cacheKey = (parentId ?? "<root>") + "/" + name;
            if (_folderIdCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var parentClause = parentId == null ? "'root' in parents" : $"'{parentId}' in parents";
            var query = $"mimeType = 'application/vnd.google-apps.folder' and trashed = false " +
                        $"and name = {EscapeForQuery(name)} and {parentClause}";

            var list = _drive!.Files.List();
            list.Q = query;
            list.Fields = "files(id,name)";
            list.PageSize = 5;
            list.Spaces = "drive";

            var found = await list.ExecuteAsync(ct).ConfigureAwait(false);
            if (found.Files != null && found.Files.Count > 0)
            {
                var id = found.Files[0].Id;
                _folderIdCache[cacheKey] = id;
                return id;
            }

            var folder = new DriveFile
            {
                Name = name,
                MimeType = "application/vnd.google-apps.folder",
                Parents = parentId == null ? null : new List<string> { parentId },
                AppProperties = new Dictionary<string, string> { ["octofetch"] = "true" },
            };

            var createReq = _drive.Files.Create(folder);
            createReq.Fields = "id,name";
            var created = await createReq.ExecuteAsync(ct).ConfigureAwait(false);
            _folderIdCache[cacheKey] = created.Id;
            return created.Id;
        }

        private static string EscapeForQuery(string value)
            => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

        private async Task PopulateAccountInfoAsync(CancellationToken ct)
        {
            if (_drive == null) { _accountEmail = null; _accountDisplayName = null; return; }

            try
            {
                var about = _drive.About.Get();
                about.Fields = "user(emailAddress,displayName)";
                var info = await about.ExecuteAsync(ct).ConfigureAwait(false);
                _accountEmail = info?.User?.EmailAddress;
                _accountDisplayName = info?.User?.DisplayName;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive: failed to fetch account info", ex);
                _accountEmail = null;
                _accountDisplayName = null;
            }
            finally
            {
                _onAccountInfo(_accountEmail, _accountDisplayName);
            }
        }

        private void RaiseConnectionChanged()
        {
            try { ConnectionChanged?.Invoke(); }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Drive ConnectionChanged handler threw", ex);
            }
        }

        private static string GuessContentType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".webm" => "video/webm",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".flac" => "audio/flac",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".opus" => "audio/opus",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".rar" => "application/vnd.rar",
                ".7z" => "application/x-7z-compressed",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".txt" => "text/plain",
                ".json" => "application/json",
                ".exe" => "application/vnd.microsoft.portable-executable",
                _ => "application/octet-stream",
            };
        }
    }

    internal sealed class OctoFetchDriveHttpClientFactory : HttpClientFactory
    {
        private readonly Func<bool> _allowInsecureSslProvider;
        private readonly IMitmService? _mitm;

        public OctoFetchDriveHttpClientFactory(
            Func<bool> allowInsecureSslProvider,
            IMitmService? mitm)
        {
            _allowInsecureSslProvider = allowInsecureSslProvider;
            _mitm = mitm;
        }

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args)
        {
            var handler = new HttpClientHandler();
            try
            {
                if (handler.SupportsAutomaticDecompression)
                    handler.AutomaticDecompression =
                        System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;

                if (_mitm != null)
                {
                    handler.UseProxy = true;
                    handler.Proxy = new DynamicMitmProxy(_mitm);
                }

                if (_allowInsecureSslProvider())
                {
                    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                }
                else if (_mitm != null)
                {
                    var mitm = _mitm;
                    handler.ServerCertificateCustomValidationCallback =
                        (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None
                            || mitm.IsRunning;
                }
            }
            catch
            {
            }
            return handler;
        }
    }
}
