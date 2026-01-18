using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PS5Upload
{
    // Protocol commands
    public enum Command : byte
    {
        Ping = 0x01,
        ListStorage = 0x02,
        ListDir = 0x03,
        CreateDir = 0x04,
        DeleteFile = 0x05,
        DeleteDir = 0x06,
        Rename = 0x07,
        CopyFile = 0x08,
        MoveFile = 0x09,
        StartUpload = 0x10,
        UploadChunk = 0x11,
        EndUpload = 0x12,
        DownloadFile = 0x13,
        Shutdown = 0xFF
    }

    // Protocol responses
    public enum Response : byte
    {
        Ok = 0x01,
        Error = 0x02,
        Data = 0x03,
        Ready = 0x04,
        Progress = 0x05
    }

    public class PS5Protocol : IDisposable
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private const int BufferSize = 8 * 1024 * 1024; // 8MB chunks for maximum throughput

        public bool IsConnected => _client?.Connected ?? false;

        public async Task<bool> ConnectAsync(string ipAddress, int port = 9113)
        {
            try
            {
                _client = new TcpClient();
                _client.ReceiveBufferSize = 128 * 1024 * 1024; // 128MB for maximum download throughput
                _client.SendBufferSize = 128 * 1024 * 1024; // 128MB for maximum upload throughput
                _client.NoDelay = true;
                _client.LingerState = new System.Net.Sockets.LingerOption(false, 0);

                // BUG FIX #1: Add 5 second timeout for connection to fail fast on wrong IP
                var connectTask = _client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(5000); // 5 second timeout
                
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    // Timeout occurred
                    _client?.Close();
                    _client?.Dispose();
                    _client = null;
                    return false;
                }
                
                // Check if connection actually succeeded
                if (!_client.Connected)
                {
                    return false;
                }
                
                _stream = _client.GetStream();
                return true;
            }
            catch
            {
                _client?.Close();
                _client?.Dispose();
                _client = null;
                return false;
            }
        }

        public void Disconnect()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
        }

        private async Task SendCommandAsync(Command cmd, byte[]? data = null)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            byte[] header = new byte[5];
            header[0] = (byte)cmd;
            
            uint dataLen = data != null ? (uint)data.Length : 0;
            BitConverter.GetBytes(dataLen).CopyTo(header, 1);

            await _stream.WriteAsync(header, 0, 5);
            if (data != null && data.Length > 0)
            {
                await _stream.WriteAsync(data, 0, data.Length);
            }
        }

        private async Task<(Response response, byte[] data)> ReceiveResponseAsync()
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            byte[] header = new byte[5];
            await ReadExactAsync(header, 5);

            Response response = (Response)header[0];
            uint dataLen = BitConverter.ToUInt32(header, 1);

            byte[] data = new byte[dataLen];
            if (dataLen > 0)
            {
                await ReadExactAsync(data, (int)dataLen);
            }

            return (response, data);
        }

        private async Task ReadExactAsync(byte[] buffer, int count, int timeoutMs = 120000)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            int offset = 0;
            using var cts = new CancellationTokenSource(timeoutMs);
            
            try
            {
                while (offset < count)
                {
                    int read = await _stream.ReadAsync(buffer, offset, count - offset, cts.Token);
                    if (read == 0)
                    {
                        // Give PS5 a moment to recover before declaring connection dead
                        await Task.Delay(100);
                        read = await _stream.ReadAsync(buffer, offset, count - offset, cts.Token);
                        if (read == 0) throw new IOException("Connection closed");
                    }
                    offset += read;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Read timeout");
            }
        }

        public async Task<bool> PingAsync()
        {
            await SendCommandAsync(Command.Ping);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<StorageInfo[]> ListStorageAsync()
        {
            await SendCommandAsync(Command.ListStorage);
            var (response, data) = await ReceiveResponseAsync();

            if (response != Response.Data) return Array.Empty<StorageInfo>();

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            int count = br.ReadInt32();
            var result = new StorageInfo[count];

            for (int i = 0; i < count; i++)
            {
                ushort pathLen = br.ReadUInt16();
                string path = Encoding.UTF8.GetString(br.ReadBytes(pathLen));
                long total = br.ReadInt64();
                long free = br.ReadInt64();

                result[i] = new StorageInfo { Path = path, TotalBytes = total, FreeBytes = free };
            }

            return result;
        }

        public async Task<FileEntry[]> ListDirAsync(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            await SendCommandAsync(Command.ListDir, pathBytes);
            var (response, data) = await ReceiveResponseAsync();

            if (response != Response.Data) return Array.Empty<FileEntry>();

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            int count = br.ReadInt32();
            
            // Handle empty directory
            if (count <= 0) return Array.Empty<FileEntry>();
            
            var result = new FileEntry[count];

            for (int i = 0; i < count; i++)
            {
                byte type = br.ReadByte();
                ushort nameLen = br.ReadUInt16();
                string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                long size = br.ReadInt64();
                long timestamp = br.ReadInt64();

                // Clamp timestamp to valid DateTimeOffset range
                DateTime dt;
                try
                {
                    if (timestamp < -62135596800 || timestamp > 253402300799)
                    {
                        dt = DateTime.Now; // Use current time for invalid timestamps
                    }
                    else
                    {
                        dt = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                    }
                }
                catch
                {
                    dt = DateTime.Now;
                }

                result[i] = new FileEntry
                {
                    Name = name,
                    IsDirectory = type == 1,
                    Size = size,
                    Timestamp = dt
                };
            }

            return result;
        }

        public async Task<bool> CreateDirAsync(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            await SendCommandAsync(Command.CreateDir, pathBytes);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<bool> DeleteFileAsync(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            await SendCommandAsync(Command.DeleteFile, pathBytes);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public event Action<string>? OnProgressMessage;

        public async Task<bool> DeleteDirAsync(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            await SendCommandAsync(Command.DeleteDir, pathBytes);
            
            // Receive initial OK response
            var (response, _) = await ReceiveResponseAsync();
            if (response != Response.Ok)
            {
                return false;
            }
            
            // Now listen for progress messages synchronously
            // Keep reading until we get the final OK/ERROR response
            bool deletionComplete = false;
            
            try
            {
                while (!deletionComplete)
                {
                    var (progressResponse, progressData) = await ReceiveResponseAsync();
                    
                    if (progressResponse == Response.Progress)
                    {
                        string message = Encoding.UTF8.GetString(progressData).TrimEnd('\0');
                        OnProgressMessage?.Invoke(message);
                    }
                    else if (progressResponse == Response.Ok)
                    {
                        // Final OK response received - deletion complete
                        OnProgressMessage?.Invoke("🔚 Received final OK - deletion complete");
                        deletionComplete = true;
                    }
                    else if (progressResponse == Response.Error)
                    {
                        // Error response received
                        OnProgressMessage?.Invoke("❌ Received error response");
                        deletionComplete = true;
                    }
                    else
                    {
                        // Unexpected response - log it
                        OnProgressMessage?.Invoke($"⚠️ Unexpected response: {progressResponse}");
                        deletionComplete = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Connection closed - log it
                OnProgressMessage?.Invoke($"⚠️ Connection closed: {ex.Message}");
            }
            
            // Give server a moment to fully close the deletion thread
            // CRITICAL: Increased delay to prevent race condition with next delete
            await Task.Delay(500);
            
            return true;
        }

        public async Task<bool> RenameAsync(string oldPath, string newPath)
        {
            byte[] oldBytes = Encoding.UTF8.GetBytes(oldPath + "\0");
            byte[] newBytes = Encoding.UTF8.GetBytes(newPath + "\0");
            byte[] data = new byte[oldBytes.Length + newBytes.Length];
            Array.Copy(oldBytes, 0, data, 0, oldBytes.Length);
            Array.Copy(newBytes, 0, data, oldBytes.Length, newBytes.Length);
            
            await SendCommandAsync(Command.Rename, data);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<bool> CopyFileAsync(string srcPath, string dstPath)
        {
            byte[] srcBytes = Encoding.UTF8.GetBytes(srcPath + "\0");
            byte[] dstBytes = Encoding.UTF8.GetBytes(dstPath + "\0");
            byte[] data = new byte[srcBytes.Length + dstBytes.Length];
            Array.Copy(srcBytes, 0, data, 0, srcBytes.Length);
            Array.Copy(dstBytes, 0, data, srcBytes.Length, dstBytes.Length);
            
            await SendCommandAsync(Command.CopyFile, data);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<bool> MoveFileAsync(string srcPath, string dstPath)
        {
            byte[] srcBytes = Encoding.UTF8.GetBytes(srcPath + "\0");
            byte[] dstBytes = Encoding.UTF8.GetBytes(dstPath + "\0");
            byte[] data = new byte[srcBytes.Length + dstBytes.Length];
            Array.Copy(srcBytes, 0, data, 0, srcBytes.Length);
            Array.Copy(dstBytes, 0, data, srcBytes.Length, dstBytes.Length);
            
            await SendCommandAsync(Command.MoveFile, data);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<bool> UploadFileAsync(string localPath, string remotePath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default, long chunkOffset = 0, long chunkSize = 0)
        {
            FileInfo fileInfo = new FileInfo(localPath);
            if (!fileInfo.Exists) return false;

            // Determine actual upload size
            long uploadSize = chunkSize > 0 ? chunkSize : fileInfo.Length;

            // Send START_UPLOAD with optional chunk offset
            byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath);
            byte[] startData = new byte[pathBytes.Length + 1 + 8 + 8]; // path + null + size + offset
            Array.Copy(pathBytes, 0, startData, 0, pathBytes.Length);
            BitConverter.GetBytes(fileInfo.Length).CopyTo(startData, pathBytes.Length + 1);
            BitConverter.GetBytes(chunkOffset).CopyTo(startData, pathBytes.Length + 9);

            await SendCommandAsync(Command.StartUpload, startData);
            var (response, _) = await ReceiveResponseAsync();

            if (response != Response.Ready) return false;

            // Upload chunks - simple async approach
            long totalSent = 0;
            var startTime = DateTime.Now;
            double avgSpeed = 0;

            byte[] sendBuffer = new byte[5 + BufferSize];
            sendBuffer[0] = (byte)Command.UploadChunk;
            
            using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            {
                // Seek to chunk offset if specified
                if (chunkOffset > 0)
                {
                    fs.Seek(chunkOffset, SeekOrigin.Begin);
                }
                
                int chunksSent = 0;
                int bytesRead;
                long bytesRemaining = uploadSize;
                
                while (bytesRemaining > 0 && (bytesRead = await fs.ReadAsync(sendBuffer, 5, (int)Math.Min(BufferSize, bytesRemaining), cancellationToken)) > 0)
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    
                    BitConverter.GetBytes((uint)bytesRead).CopyTo(sendBuffer, 1);
                    
                    if (_stream == null) return false;
                    
                    // FIX: Add timeout to WriteAsync to prevent hanging if PS5 becomes unresponsive
                    // 30 second timeout per chunk write - should be more than enough for 4MB
                    using var writeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeTimeout.Token);
                    
                    try
                    {
                        await _stream.WriteAsync(sendBuffer, 0, 5 + bytesRead, linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (writeTimeout.IsCancellationRequested)
                    {
                        // Write timeout - PS5 is unresponsive
                        return false;
                    }
                    
                    totalSent += bytesRead;
                    bytesRemaining -= bytesRead;
                    chunksSent++;
                    
                    if (chunksSent % 5 == 0 || bytesRemaining == 0)
                    {
                        var elapsed = DateTime.Now - startTime;
                        double currentSpeed = elapsed.TotalSeconds > 0 ? totalSent / elapsed.TotalSeconds : 0;
                        avgSpeed = currentSpeed;
                        
                        TimeSpan eta = currentSpeed > 0 
                            ? TimeSpan.FromSeconds(bytesRemaining / currentSpeed) 
                            : TimeSpan.Zero;

                        progress?.Report(new UploadProgress
                        {
                            BytesSent = chunkOffset + totalSent,
                            TotalBytes = fileInfo.Length,
                            SpeedBytesPerSecond = currentSpeed,
                            AverageSpeedBytesPerSecond = avgSpeed,
                            ElapsedTime = elapsed,
                            EstimatedTimeRemaining = eta,
                            CurrentFileName = fileInfo.Name
                        });
                    }
                }
            }

            // Send END_UPLOAD and wait for response
            // CRITICAL: Must wait for response to avoid protocol desync on chunked uploads
            try
            {
                await SendCommandAsync(Command.EndUpload);
                var (endResponse, _) = await ReceiveResponseAsync();
                return endResponse == Response.Ok;
            }
            catch
            {
                return false; // Connection issue means upload failed
            }
        }

        public async Task<bool> DownloadFileAsync(string remotePath, string localPath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                // Send download command
                byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath);
                await SendCommandAsync(Command.DownloadFile, pathBytes);
                
                // Receive response header (5 bytes: 1 response + 4 data_len)
                byte[] header = new byte[5];
                await ReadExactAsync(header, 5);
                
                Response response = (Response)header[0];
                uint dataLen = BitConverter.ToUInt32(header, 1);
                
                // Check if error response
                if (response == Response.Error)
                {
                    if (dataLen > 0)
                    {
                        byte[] errorMsg = new byte[dataLen];
                        await ReadExactAsync(errorMsg, (int)dataLen);
                    }
                    return false;
                }
                
                // Expecting RESP_DATA with 8-byte file size
                if (response != Response.Data || dataLen != 8)
                {
                    return false;
                }
                
                // Read file size (8 bytes)
                byte[] sizeBytes = new byte[8];
                await ReadExactAsync(sizeBytes, 8);
                long fileSize = BitConverter.ToInt64(sizeBytes, 0);
                
                // Now read raw file data directly from socket with optimized buffering
                using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough | FileOptions.SequentialScan);
                
                byte[] buffer = new byte[BufferSize];
                long totalReceived = 0;
                var startTime = DateTime.Now;
                var lastProgressReport = DateTime.Now;
                
                while (totalReceived < fileSize)
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    
                    int toRead = (int)Math.Min(BufferSize, fileSize - totalReceived);
                    int received = await _stream!.ReadAsync(buffer, 0, toRead, cancellationToken);
                    
                    if (received == 0) break;
                    
                    await fs.WriteAsync(buffer, 0, received, cancellationToken);
                    totalReceived += received;
                    
                    // Report progress every 16MB or every 200ms for smooth UI updates
                    var now = DateTime.Now;
                    if (totalReceived % (16 * 1024 * 1024) < BufferSize || (now - lastProgressReport).TotalMilliseconds >= 200 || totalReceived == fileSize)
                    {
                        var elapsed = now - startTime;
                        double speed = elapsed.TotalSeconds > 0 ? totalReceived / elapsed.TotalSeconds : 0;
                        
                        progress?.Report(new UploadProgress
                        {
                            BytesSent = totalReceived,
                            TotalBytes = fileSize,
                            SpeedBytesPerSecond = speed,
                            AverageSpeedBytesPerSecond = speed,
                            ElapsedTime = elapsed,
                            EstimatedTimeRemaining = speed > 0 ? TimeSpan.FromSeconds((fileSize - totalReceived) / speed) : TimeSpan.Zero,
                            CurrentFileName = Path.GetFileName(localPath)
                        });
                        
                        lastProgressReport = now;
                    }
                }
                
                return totalReceived == fileSize;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download error: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    public class StorageInfo
    {
        public string Path { get; set; } = "";
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
    }

    public class FileEntry
    {
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class UploadProgress
    {
        public long BytesSent { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public double AverageSpeedBytesPerSecond { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
        public string CurrentFileName { get; set; } = "";
        
        // For folder progress
        public int CurrentFileIndex { get; set; }
        public int TotalFiles { get; set; }
        public long TotalFolderBytes { get; set; }
        public long TotalFolderBytesSent { get; set; }
    }
    
    // High-speed parallel uploader using multiple connections
    public class ParallelUploader : IDisposable
    {
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly int _connectionCount;
        private const int ChunkSize = 4 * 1024 * 1024; // 4MB chunks per connection
        
        public ParallelUploader(string ipAddress, int port = 9113, int connectionCount = 4)
        {
            _ipAddress = ipAddress;
            _port = port;
            _connectionCount = connectionCount;
        }
        
        public async Task<bool> UploadFileAsync(string localPath, string remotePath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            FileInfo fileInfo = new FileInfo(localPath);
            if (!fileInfo.Exists) return false;
            
            // For small files, use single connection
            if (fileInfo.Length < ChunkSize * 2)
            {
                using var protocol = new PS5Protocol();
                if (!await protocol.ConnectAsync(_ipAddress, _port)) return false;
                return await protocol.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
            }
            
            // For large files, use parallel connections
            // This requires server support for chunked/parallel uploads
            // For now, fall back to single connection
            using var proto = new PS5Protocol();
            if (!await proto.ConnectAsync(_ipAddress, _port)) return false;
            return await proto.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
        }
        
        public void Dispose() { }
    }
}
