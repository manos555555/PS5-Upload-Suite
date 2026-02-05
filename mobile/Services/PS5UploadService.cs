#nullable enable
using PS5Upload;

namespace PS5UploadMobile.Services
{
    public class PS5UploadService
    {
        private string _ps5Ip = "192.168.0.160";
        private int _port = 9113;
        private PS5Protocol? _protocol;

        public event EventHandler<string>? LogMessage;
        public event EventHandler<int>? ProgressChanged;

        public PS5UploadService()
        {
            _protocol = new PS5Protocol();
        }

        public string PS5Ip
        {
            get => _ps5Ip;
            set => _ps5Ip = value;
        }

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        public bool IsConnected => _protocol?.IsConnected ?? false;

        public async Task<bool> ConnectAsync()
        {
            try
            {
                _protocol = new PS5Protocol();
                bool connected = await _protocol.ConnectAsync(_ps5Ip, _port);

                if (connected)
                {
                    LogMessage?.Invoke(this, $"Connected to PS5 at {_ps5Ip}:{_port}");
                    return true;
                }
                else
                {
                    LogMessage?.Invoke(this, "Failed to connect to PS5");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Connection error: {ex.Message}");
                return false;
            }
        }

        public Task DisconnectAsync()
        {
            if (_protocol != null)
            {
                _protocol.Disconnect();
                LogMessage?.Invoke(this, "Disconnected from PS5");
            }
            return Task.CompletedTask;
        }

        public async Task<List<FileItem>> ListDirectoryAsync(string path)
        {
            var files = new List<FileItem>();

            if (_protocol == null || !_protocol.IsConnected)
            {
                LogMessage?.Invoke(this, "Not connected to PS5");
                return files;
            }

            try
            {
                var entries = await _protocol.ListDirAsync(path);

                foreach (var entry in entries)
                {
                    string fullPath = path.TrimEnd('/') + "/" + entry.Name;
                    if (entry.IsDirectory) fullPath += "/";

                    files.Add(new FileItem
                    {
                        Name = entry.Name,
                        Size = entry.IsDirectory ? "" : FormatFileSize(entry.Size),
                        Type = entry.IsDirectory ? "DIR" : "FILE",
                        Icon = entry.IsDirectory ? "📁" : "📄",
                        IsDirectory = entry.IsDirectory,
                        FullPath = fullPath
                    });
                }

                LogMessage?.Invoke(this, $"Listed {files.Count} items in {path}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"List error: {ex.Message}");
            }

            return files;
        }

        public async Task<bool> UploadFileAsync(string localPath, string remotePath)
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                LogMessage?.Invoke(this, "Not connected to PS5");
                return false;
            }

            try
            {
                var fileInfo = new FileInfo(localPath);
                LogMessage?.Invoke(this, $"Uploading {fileInfo.Name} ({FormatFileSize(fileInfo.Length)})...");

                var progress = new Progress<UploadProgress>(p =>
                {
                    int percent = p.TotalBytes > 0 ? (int)((p.BytesSent * 100) / p.TotalBytes) : 0;
                    ProgressChanged?.Invoke(this, percent);
                });

                bool success = await _protocol.UploadFileAsync(localPath, remotePath, progress);

                if (success)
                {
                    LogMessage?.Invoke(this, $"Upload complete: {fileInfo.Name}");
                    return true;
                }
                else
                {
                    LogMessage?.Invoke(this, "Upload failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Upload error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DownloadFileAsync(string remotePath, string localPath)
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                LogMessage?.Invoke(this, "Not connected to PS5");
                return false;
            }

            try
            {
                LogMessage?.Invoke(this, $"Downloading {Path.GetFileName(remotePath)}...");

                var progress = new Progress<UploadProgress>(p =>
                {
                    int percent = p.TotalBytes > 0 ? (int)((p.BytesSent * 100) / p.TotalBytes) : 0;
                    ProgressChanged?.Invoke(this, percent);
                });

                bool success = await _protocol.DownloadFileAsync(remotePath, localPath, progress);

                if (success)
                {
                    LogMessage?.Invoke(this, $"Download complete: {Path.GetFileName(remotePath)}");
                    return true;
                }
                else
                {
                    LogMessage?.Invoke(this, "Download failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Download error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DeleteFileAsync(string remotePath)
        {
            if (_protocol == null || !_protocol.IsConnected)
            {
                LogMessage?.Invoke(this, "Not connected to PS5");
                return false;
            }

            try
            {
                bool success = await _protocol.DeleteFileAsync(remotePath);

                if (success)
                {
                    LogMessage?.Invoke(this, $"Deleted: {remotePath}");
                    return true;
                }
                else
                {
                    LogMessage?.Invoke(this, "Delete failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"Delete error: {ex.Message}");
                return false;
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class FileItem
    {
        public string Name { get; set; } = "";
        public string Size { get; set; } = "";
        public string Type { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public string FullPath { get; set; } = "";
    }
}
