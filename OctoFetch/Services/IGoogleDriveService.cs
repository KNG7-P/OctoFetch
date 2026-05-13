using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public class GoogleDriveUploadResult
    {
        public string FileId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ShareUrl { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public sealed class DriveActionsCredentials
    {
        public string RefreshToken { get; }
        public string ClientId { get; }
        public string ClientSecret { get; }

        public DriveActionsCredentials(string refreshToken, string clientId, string clientSecret)
        {
            RefreshToken = refreshToken;
            ClientId = clientId;
            ClientSecret = clientSecret;
        }
    }

    public interface IGoogleDriveService
    {
        bool IsConnected { get; }
        string? AccountEmail { get; }
        string? AccountDisplayName { get; }

        event Action? ConnectionChanged;

        Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default);

        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

        Task DisconnectAsync(CancellationToken cancellationToken = default);

        Task<GoogleDriveUploadResult> UploadFileAsync(
            string localPath,
            string category,
            string? displayFileName,
            Action<long, long>? onProgress,
            CancellationToken cancellationToken = default);

        DriveActionsCredentials? GetActionsCredentials();

        Task<IReadOnlyList<DriveFileItem>> ListUploadedFilesAsync(CancellationToken cancellationToken = default);

        Task DownloadFileAsync(
            string fileId,
            string destinationPath,
            Action<long, long>? onProgress,
            long knownSize = 0,
            long resumeFromByte = 0,
            CancellationToken cancellationToken = default);

        Task<string?> RenameFileAsync(
            string fileId,
            string newName,
            CancellationToken cancellationToken = default);

        Task<bool> DeleteFileAsync(
            string fileId,
            CancellationToken cancellationToken = default);
    }
}
