using System;
using System.Buffers;
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
        ShellOpen = 0x20,
        ShellExec = 0x21,
        ShellInterrupt = 0x22,
        ShellClose = 0x23,
        IndexStart = 0x40,
        IndexStatus = 0x41,
        SearchIndex = 0x42,
        IndexCancel = 0x43,
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
        private const int BufferSize = 4 * 1024 * 1024; // 4MB buffer - maximum throughput for chunk uploads

        public bool IsConnected => _client?.Connected ?? false;

        public async Task<bool> ConnectAsync(string ipAddress, int port = 9113)
        {
            try
            {
                _client = new TcpClient();
                _client.ReceiveBufferSize = 16 * 1024 * 1024; // 16MB - matches payload SO_RCVBUF setting
                _client.SendBufferSize = 16 * 1024 * 1024; // 16MB - matches payload SO_RCVBUF setting
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

                // Await the connect task to observe any exception it produced (prevents unobserved task crashes)
                await connectTask.ConfigureAwait(false);
                
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

        public async Task<PS5StorageInfo?> ListStorageAsync()
        {
            try
            {
                await SendCommandAsync(Command.ListStorage);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                {
                    Console.WriteLine($"[DEBUG] ListStorage: Response is not Data, got {response}");
                    return null;
                }

                // Parse response: total|free|available|reserved|mounted_games|user_data|real_free
                string responseStr = Encoding.UTF8.GetString(data);
                Console.WriteLine($"[DEBUG] ListStorage raw response: '{responseStr}'");
                
                string[] parts = responseStr.Split('|');
                Console.WriteLine($"[DEBUG] ListStorage parts count: {parts.Length}");
                
                if (parts.Length < 7)
                {
                    Console.WriteLine($"[DEBUG] ListStorage: Expected at least 7 parts, got {parts.Length}");
                    for (int i = 0; i < parts.Length; i++)
                    {
                        Console.WriteLine($"[DEBUG]   Part[{i}]: '{parts[i]}'");
                    }
                    return null;
                }
                
                // Get path if available (part 8)
                string storagePath = (parts.Length >= 8) ? parts[7].Trim() : "unknown";

                return new PS5StorageInfo
                {
                    TotalBytes = ulong.Parse(parts[0].Trim()),
                    FreeBytes = ulong.Parse(parts[1].Trim()),
                    AvailableBytes = ulong.Parse(parts[2].Trim()),
                    ReservedBytes = ulong.Parse(parts[3].Trim()),
                    MountedGamesSize = ulong.Parse(parts[4].Trim()),
                    UserDataSize = ulong.Parse(parts[5].Trim()),
                    RealFreeSpace = ulong.Parse(parts[6].Trim()),
                    StoragePath = storagePath
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] ListStorage exception: {ex.Message}");
                Console.WriteLine($"[DEBUG] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public async Task<FileEntry[]> ListDirAsync(string path)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            await SendCommandAsync(Command.ListDir, pathBytes);
            
            // Payload sends ONLY RESP_DATA - no RESP_OK after!
            var (response, data) = await ReceiveResponseAsync();

            if (response != Response.Data)
            {
                return Array.Empty<FileEntry>();
            }

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            int count = br.ReadInt32();
            
            // Handle empty directory
            if (count <= 0)
            {
                return Array.Empty<FileEntry>();
            }
            
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
            
            // Payload does NOT send initial OK - goes straight to progress messages
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
            var (response, responseData) = await ReceiveResponseAsync();

            // Debug logging
            OnProgressMessage?.Invoke($"[DEBUG] START_UPLOAD Response: {response} (Expected: {Response.Ready})");
            
            if (response != Response.Ready)
            {
                OnProgressMessage?.Invoke($"[DEBUG] Upload failed - wrong response code");
                return false;
            }

            // Upload chunks - simple async approach
            long totalSent = 0;
            var startTime = DateTime.Now;
            double avgSpeed = 0;

            byte[][] sendBuffers = new byte[2][];
            sendBuffers[0] = ArrayPool<byte>.Shared.Rent(5 + BufferSize);
            sendBuffers[1] = ArrayPool<byte>.Shared.Rent(5 + BufferSize);
            
            try
            {
                // Use RandomAccess for chunked uploads to avoid disk I/O contention when multiple workers read same file
                FileOptions fileOptions = chunkOffset > 0 ? FileOptions.RandomAccess : FileOptions.SequentialScan;
                using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, fileOptions))
                {
                    if (chunkOffset > 0)
                    {
                        fs.Seek(chunkOffset, SeekOrigin.Begin);
                    }
                    
                    int activeBufferIndex = 0;
                    long bytesRemaining = uploadSize;
                    int bytesRead = await fs.ReadAsync(sendBuffers[activeBufferIndex], 5, (int)Math.Min(BufferSize, bytesRemaining), cancellationToken);
                    int chunksSent = 0;
                    
                    while (bytesRemaining > 0 && bytesRead > 0)
                    {
                        if (_stream == null) return false;
                        if (cancellationToken.IsCancellationRequested) return false;
                        
                        byte[] writeBuffer = sendBuffers[activeBufferIndex];
                        writeBuffer[0] = (byte)Command.UploadChunk;
                        BitConverter.GetBytes((uint)bytesRead).CopyTo(writeBuffer, 1);
                        
                        long remainingAfterCurrent = bytesRemaining - bytesRead;
                        int nextBufferIndex = 1 - activeBufferIndex;
                        Task<int>? pendingReadTask = null;
                        if (remainingAfterCurrent > 0)
                        {
                            int nextReadLength = (int)Math.Min(BufferSize, remainingAfterCurrent);
                            pendingReadTask = fs.ReadAsync(sendBuffers[nextBufferIndex], 5, nextReadLength, cancellationToken);
                        }
                        
                        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeTimeout.Token);
                        
                        try
                        {
                            await _stream.WriteAsync(writeBuffer, 0, 5 + bytesRead, linkedCts.Token);
                        }
                        catch (OperationCanceledException) when (writeTimeout.IsCancellationRequested)
                        {
                            if (pendingReadTask != null)
                            {
                                try { await pendingReadTask; } catch { }
                            }
                            return false;
                        }
                        
                        totalSent += bytesRead;
                        bytesRemaining -= bytesRead;
                        chunksSent++;
                        
                        if (chunksSent % 20 == 0 || bytesRemaining == 0)
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
                        
                        if (pendingReadTask == null)
                        {
                            break;
                        }
                        
                        activeBufferIndex = 1 - activeBufferIndex;
                        bytesRead = await pendingReadTask;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sendBuffers[0]);
                ArrayPool<byte>.Shared.Return(sendBuffers[1]);
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
                
                // Now read raw file data directly from socket
                using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                
                byte[] buffer = new byte[BufferSize];
                long totalReceived = 0;
                var startTime = DateTime.Now;
                
                while (totalReceived < fileSize)
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    
                    int toRead = (int)Math.Min(BufferSize, fileSize - totalReceived);
                    int received = await _stream!.ReadAsync(buffer, 0, toRead, cancellationToken);
                    
                    if (received == 0) break;
                    
                    await fs.WriteAsync(buffer, 0, received, cancellationToken);
                    totalReceived += received;
                    
                    // Report progress every 5MB or at completion
                    if (totalReceived % (5 * 1024 * 1024) < BufferSize || totalReceived == fileSize)
                    {
                        var elapsed = DateTime.Now - startTime;
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

        public async Task<bool> OpenShellAsync()
        {
            await SendCommandAsync(Command.ShellOpen);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<string> ExecuteShellCommandAsync(string command)
        {
            byte[] cmdBytes = Encoding.UTF8.GetBytes(command + "\0");
            await SendCommandAsync(Command.ShellExec, cmdBytes);
            
            // Payload sends multiple RESP_DATA responses (e.g., ls sends one per file)
            // Read all RESP_DATA until we get RESP_OK or RESP_ERROR
            var outputBuilder = new System.Text.StringBuilder();
            
            while (true)
            {
                var (response, data) = await ReceiveResponseAsync();
                
                if (response == Response.Data)
                {
                    // Accumulate output from multiple RESP_DATA responses
                    string chunk = Encoding.UTF8.GetString(data).TrimEnd('\0');
                    outputBuilder.Append(chunk);
                }
                else if (response == Response.Ok)
                {
                    // End of output - return accumulated data
                    return outputBuilder.ToString();
                }
                else if (response == Response.Error)
                {
                    string error = data.Length > 0 ? Encoding.UTF8.GetString(data).TrimEnd('\0') : "Command failed";
                    return $"Error: {error}";
                }
                else
                {
                    // Unexpected response
                    break;
                }
            }
            
            return outputBuilder.ToString();
        }

        public async Task<bool> CloseShellAsync()
        {
            await SendCommandAsync(Command.ShellClose);
            var (response, _) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        // Index methods
        public async Task<bool> StartIndexAsync(string paths)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(paths + "\0");
            await SendCommandAsync(Command.IndexStart, pathBytes);
            var (response, data) = await ReceiveResponseAsync();
            return response == Response.Ok;
        }

        public async Task<string> GetIndexStatusAsync()
        {
            await SendCommandAsync(Command.IndexStatus);
            var (response, data) = await ReceiveResponseAsync();
            if (response == Response.Ok && data.Length > 0)
            {
                return Encoding.UTF8.GetString(data).TrimEnd('\0');
            }
            return "Unknown";
        }

        public async Task<SearchResult[]> SearchIndexAsync(string query)
        {
            byte[] queryBytes = Encoding.UTF8.GetBytes(query + "\0");
            await SendCommandAsync(Command.SearchIndex, queryBytes);
            
            var results = new System.Collections.Generic.List<SearchResult>();
            
            // Payload sends: RESP_DATA(1) + raw_data for each result, then RESP_OK(1) + data_len(4) + message
            if (_stream == null) return Array.Empty<SearchResult>();
            
            while (true)
            {
                // Read response byte
                byte[] respBuf = new byte[1];
                int read = await _stream.ReadAsync(respBuf, 0, 1);
                if (read == 0) break;
                
                Response response = (Response)respBuf[0];
                
                if (response == Response.Data)
                {
                    // Read raw data: path_len(4) + path + name_len(4) + name + size(8) + mtime(8) + is_dir(1)
                    byte[] pathLenBuf = new byte[4];
                    await _stream.ReadAsync(pathLenBuf, 0, 4);
                    uint pathLen = BitConverter.ToUInt32(pathLenBuf, 0);
                    
                    byte[] pathBuf = new byte[pathLen];
                    await _stream.ReadAsync(pathBuf, 0, (int)pathLen);
                    string path = Encoding.UTF8.GetString(pathBuf);
                    
                    byte[] nameLenBuf = new byte[4];
                    await _stream.ReadAsync(nameLenBuf, 0, 4);
                    uint nameLen = BitConverter.ToUInt32(nameLenBuf, 0);
                    
                    byte[] nameBuf = new byte[nameLen];
                    await _stream.ReadAsync(nameBuf, 0, (int)nameLen);
                    string name = Encoding.UTF8.GetString(nameBuf);
                    
                    byte[] sizeBuf = new byte[8];
                    await _stream.ReadAsync(sizeBuf, 0, 8);
                    long size = BitConverter.ToInt64(sizeBuf, 0);
                    
                    byte[] mtimeBuf = new byte[8];
                    await _stream.ReadAsync(mtimeBuf, 0, 8);
                    long mtime = BitConverter.ToInt64(mtimeBuf, 0);
                    
                    byte[] isDirBuf = new byte[1];
                    await _stream.ReadAsync(isDirBuf, 0, 1);
                    bool isDir = isDirBuf[0] == 1;
                    
                    results.Add(new SearchResult
                    {
                        Path = path,
                        Name = name,
                        Size = size,
                        Modified = DateTimeOffset.FromUnixTimeSeconds(mtime).DateTime,
                        IsDirectory = isDir
                    });
                }
                else if (response == Response.Ok)
                {
                    // Read and discard the OK message
                    byte[] dataLenBuf = new byte[4];
                    await _stream.ReadAsync(dataLenBuf, 0, 4);
                    uint dataLen = BitConverter.ToUInt32(dataLenBuf, 0);
                    if (dataLen > 0)
                    {
                        byte[] msgBuf = new byte[dataLen];
                        await _stream.ReadAsync(msgBuf, 0, (int)dataLen);
                    }
                    break;
                }
                else if (response == Response.Error)
                {
                    // Read error message
                    byte[] dataLenBuf = new byte[4];
                    await _stream.ReadAsync(dataLenBuf, 0, 4);
                    uint dataLen = BitConverter.ToUInt32(dataLenBuf, 0);
                    if (dataLen > 0)
                    {
                        byte[] msgBuf = new byte[dataLen];
                        await _stream.ReadAsync(msgBuf, 0, (int)dataLen);
                    }
                    break;
                }
            }
            
            return results.ToArray();
        }

        // Send payload ELF file to PS5 (port 9021)
        public static async Task<bool> SendPayloadAsync(string ipAddress, string payloadPath, int port = 9021, IProgress<long>? progress = null)
        {
            TcpClient? client = null;
            try
            {
                FileInfo fileInfo = new FileInfo(payloadPath);
                if (!fileInfo.Exists) return false;

                // Validate file extension
                string ext = fileInfo.Extension.ToLower();
                if (ext != ".elf" && ext != ".bin")
                {
                    return false;
                }

                client = new TcpClient();
                client.SendBufferSize = 8 * 1024 * 1024;
                client.ReceiveBufferSize = 8 * 1024 * 1024;
                client.NoDelay = true;
                client.SendTimeout = 10000;

                // Connect with 10 second timeout
                var connectTask = client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(10000);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    client?.Close();
                    return false;
                }

                // Wait for connect task to complete
                await connectTask;

                if (!client.Connected)
                {
                    return false;
                }

                using var stream = client.GetStream();
                using var fs = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Use larger buffer for faster transfer
                byte[] buffer = new byte[64 * 1024];
                long totalSent = 0;
                int bytesRead;

                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, bytesRead);
                    totalSent += bytesRead;
                    progress?.Report(totalSent);
                }

                await stream.FlushAsync();
                
                // Give PS5 time to process and execute payload
                await Task.Delay(500);
                
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                client?.Close();
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    public class SearchResult
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public bool IsDirectory { get; set; }
        public string SizeText => FormatFileSize(Size);
        
        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class StorageInfo
    {
        public string Path { get; set; } = "";
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
    }

    public class PS5StorageInfo
    {
        public ulong TotalBytes { get; set; }
        public ulong FreeBytes { get; set; }
        public ulong AvailableBytes { get; set; }
        public ulong ReservedBytes { get; set; }
        public ulong MountedGamesSize { get; set; }
        public ulong UserDataSize { get; set; }
        public ulong RealFreeSpace { get; set; }
        public string StoragePath { get; set; } = "unknown";

        public string TotalGB => FormatBytes(TotalBytes);
        public string FreeGB => FormatBytes(FreeBytes);
        public string RealFreeGB => FormatBytes(RealFreeSpace);
        public string MountedGamesGB => FormatBytes(MountedGamesSize);
        public string UserDataGB => FormatBytes(UserDataSize);
        public string ReservedGB => FormatBytes(ReservedBytes);

        public static string FormatBytes(ulong bytes)
        {
            // Use 1000-based (decimal) like PS5, not 1024-based (binary)
            if (bytes >= 1000UL * 1000 * 1000 * 1000)
                return $"{bytes / (1000.0 * 1000 * 1000 * 1000):F1} TB";
            if (bytes >= 1000UL * 1000 * 1000)
                return $"{bytes / (1000.0 * 1000 * 1000):F1} GB";
            if (bytes >= 1000UL * 1000)
                return $"{bytes / (1000.0 * 1000):F1} MB";
            if (bytes >= 1000UL)
                return $"{bytes / 1000.0:F1} KB";
            return $"{bytes} bytes";
        }
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
