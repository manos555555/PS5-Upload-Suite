using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        MountGames = 0x30,
        GetFileInfo = 0x31,
        GetSystemInfo = 0x32,
        VerifyFile = 0x33,
        GetHwInfo = 0x34,
        GetTemps = 0x35,
        GetRunningApps = 0x36,
        KillApp = 0x37,
        LaunchBrowser = 0x38,
        GetPowerInfo = 0x39,
        GetGameList = 0x3A,
        UnmountGame = 0x3B,
        GetGameIcon = 0x3C,
        GetGameDetails = 0x3D,
        GetGamePic = 0x3E,
        ListSaves = 0x3F,
        IndexCancel = 0x43,
        LaunchGame = 0x44,
        ListScreenshots = 0x45,
        DeleteScreenshot = 0x46,
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
        private const int BufferSize = 8 * 1024 * 1024; // 8MB buffer - matches payload BUFFER_SIZE for maximum throughput
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1); // Serialize all command-response cycles to prevent protocol desync

        public bool IsConnected => _client != null && _stream != null && _client.Connected;
        
        public string LastError { get; private set; } = "";

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
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.Ping);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            catch (Exception ex)
            {
                LastError = $"PingAsync: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<PS5StorageInfo?> ListStorageAsync()
        {
            await _commandLock.WaitAsync();
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
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get detailed file information
        public async Task<PS5FileInfo?> GetFileInfoAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.GetFileInfo, pathBytes);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                // Parse: size|mtime|atime|mode|is_dir|is_link
                string responseStr = Encoding.UTF8.GetString(data);
                string[] parts = responseStr.Split('|');
                if (parts.Length < 6) return null;

                return new PS5FileInfo
                {
                    Size = long.Parse(parts[0]),
                    ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1])).LocalDateTime,
                    AccessTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[2])).LocalDateTime,
                    Permissions = Convert.ToInt32(parts[3], 8),
                    IsDirectory = parts[4] == "1",
                    IsSymlink = parts[5] == "1"
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get PS5 system information
        public async Task<PS5SystemInfo?> GetSystemInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetSystemInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5SystemInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "hostname": info.Hostname = value; break;
                        case "server_version": info.ServerVersion = value; break;
                        case "protocol_version": int.TryParse(value, out int pv); info.ProtocolVersion = pv; break;
                        case "total_memory": ulong.TryParse(value, out ulong tm); info.TotalMemory = tm; break;
                        case "storage_total": ulong.TryParse(value, out ulong st); info.StorageTotal = st; break;
                        case "storage_free": ulong.TryParse(value, out ulong sf); info.StorageFree = sf; break;
                        case "mounted_games": int.TryParse(value, out int mg); info.MountedGames = mg; break;
                        case "index_ready": info.IndexReady = value == "1"; break;
                        case "index_files": int.TryParse(value, out int ifi); info.IndexFiles = ifi; break;
                        case "index_dirs": int.TryParse(value, out int idi); info.IndexDirs = idi; break;
                        case "server_uptime": long.TryParse(value, out long su); info.ServerUptime = su; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Verify file integrity with CRC32
        public async Task<FileVerificationResult> VerifyFileAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.VerifyFile, pathBytes);
                var (response, data) = await ReceiveResponseAsync();

                if (response == Response.Error)
                {
                    return new FileVerificationResult
                    {
                        Success = false,
                        Error = Encoding.UTF8.GetString(data)
                    };
                }

                if (response != Response.Data)
                {
                    return new FileVerificationResult
                    {
                        Success = false,
                        Error = "Unexpected response"
                    };
                }

                // Parse: CRC32|size
                string responseStr = Encoding.UTF8.GetString(data);
                string[] parts = responseStr.Split('|');
                if (parts.Length < 2)
                {
                    return new FileVerificationResult
                    {
                        Success = false,
                        Error = "Invalid response format"
                    };
                }

                return new FileVerificationResult
                {
                    Success = true,
                    CRC32 = parts[0],
                    Size = ulong.Parse(parts[1])
                };
            }
            catch (Exception ex)
            {
                return new FileVerificationResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get hardware info (model, serial, features)
        public async Task<PS5HardwareInfo?> GetHardwareInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetHwInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5HardwareInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "model": info.Model = value; break;
                        case "serial": info.Serial = value; break;
                        case "has_wlan_bt": info.HasWlanBt = value == "1"; break;
                        case "has_optical_out": info.HasOpticalOut = value == "1"; break;
                        case "hw_machine": info.HwMachine = value; break;
                        case "os": info.OsVersion = value; break;
                        case "ncpu": int.TryParse(value, out int n); info.NumCpu = n; break;
                        case "physmem": ulong.TryParse(value, out ulong pm); info.PhysMem = pm; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Store last raw diagnostic response for logging
        public string LastTempRawResponse { get; private set; } = "";

        // NEW: Get temperature and CPU info
        public async Task<PS5TemperatureInfo?> GetTemperatureInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetTemps);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                LastTempRawResponse = responseStr;
                var info = new PS5TemperatureInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "cpu_temp": int.TryParse(value, out int ct); info.CpuTemp = ct; break;
                        case "soc_temp": int.TryParse(value, out int st); info.SocTemp = st; break;
                        case "cpu_freq_mhz": long.TryParse(value, out long cf); info.CpuFreqMhz = cf; break;
                        case "soc_power_mw": uint.TryParse(value, out uint pw); info.SocPowerMw = pw; break;
                        case "cpu_usage_0": int.TryParse(value, out int c0); info.CpuUsage[0] = c0; break;
                        case "cpu_usage_1": int.TryParse(value, out int c1); info.CpuUsage[1] = c1; break;
                        case "cpu_usage_2": int.TryParse(value, out int c2); info.CpuUsage[2] = c2; break;
                        case "cpu_usage_3": int.TryParse(value, out int c3); info.CpuUsage[3] = c3; break;
                        case "cpu_usage_4": int.TryParse(value, out int c4); info.CpuUsage[4] = c4; break;
                        case "cpu_usage_5": int.TryParse(value, out int c5); info.CpuUsage[5] = c5; break;
                        case "cpu_usage_6": int.TryParse(value, out int c6); info.CpuUsage[6] = c6; break;
                        case "cpu_usage_7": int.TryParse(value, out int c7); info.CpuUsage[7] = c7; break;
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                LastError = $"GetTemperatureInfoAsync: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get list of running apps
        public async Task<List<PS5RunningApp>> GetRunningAppsAsync()
        {
            var apps = new List<PS5RunningApp>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetRunningApps);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return apps;

                string responseStr = Encoding.UTF8.GetString(data);

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line) || line == "No apps running")
                        continue;

                    var app = new PS5RunningApp();
                    foreach (var part in line.Split('|'))
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length != 2) continue;

                        switch (kv[0])
                        {
                            case "pid": int.TryParse(kv[1], out int pid); app.Pid = pid; break;
                            case "name": app.Name = kv[1]; break;
                            case "title_id": app.TitleId = kv[1]; break;
                            case "app_id": uint.TryParse(kv[1], out uint aid); app.AppId = aid; break;
                        }
                    }

                    if (!string.IsNullOrEmpty(app.TitleId))
                        apps.Add(app);
                }

                return apps;
            }
            catch
            {
                return apps;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Kill an app by title ID
        public async Task<(bool success, string message)> KillAppAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.KillApp, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Launch browser with URL
        public async Task<(bool success, string message)> LaunchBrowserAsync(string url)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(url + "\0");
                await SendCommandAsync(Command.LaunchBrowser, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get power info (operating time, boot count, power consumption)
        public async Task<PS5PowerInfo?> GetPowerInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetPowerInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5PowerInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "operating_time_sec": ulong.TryParse(value, out ulong ots); info.OperatingTimeSec = ots; break;
                        case "operating_time_hours": ulong.TryParse(value, out ulong oth); info.OperatingTimeHours = oth; break;
                        case "operating_time_minutes": ulong.TryParse(value, out ulong otm); info.OperatingTimeMinutes = otm; break;
                        case "boot_count": uint.TryParse(value, out uint bc); info.BootCount = bc; break;
                        case "power_consumption_mw": uint.TryParse(value, out uint pc); info.PowerConsumptionMw = pc; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get list of mounted games
        public async Task<List<PS5MountedGame>> GetGameListAsync()
        {
            var games = new List<PS5MountedGame>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetGameList);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return games;

                string responseStr = Encoding.UTF8.GetString(data);

                if (responseStr.Trim() == "NO_GAMES")
                    return games;

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Format: title_id|name|path|size|region|active
                    var parts = line.Split('|');
                    if (parts.Length < 6) continue;

                    var game = new PS5MountedGame
                    {
                        TitleId = parts[0],
                        Name = parts[1],
                        Path = parts[2],
                        Region = parts[4],
                        IsActive = parts[5] == "1"
                    };

                    if (ulong.TryParse(parts[3], out ulong size))
                        game.Size = size;

                    games.Add(game);
                }

                return games;
            }
            catch
            {
                return games;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: List all save games on the PS5
        public async Task<List<PS5SaveGame>> ListSavesAsync()
        {
            var saves = new List<PS5SaveGame>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ListSaves);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) return saves;

                string responseStr = Encoding.UTF8.GetString(data);
                if (responseStr.Trim() == "NO_SAVES") return saves;

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Format: title_id|user_id|save_path|size|mtime
                    var parts = line.Split('|');
                    if (parts.Length < 5) continue;

                    var save = new PS5SaveGame
                    {
                        TitleId = parts[0],
                        UserId = parts[1],
                        SavePath = parts[2],
                    };

                    if (ulong.TryParse(parts[3], out ulong sz)) save.Size = sz;
                    if (long.TryParse(parts[4], out long mt)) save.ModifiedUnixTime = mt;

                    saves.Add(save);
                }
                return saves;
            }
            catch { return saves; }
            finally { _commandLock.Release(); }
        }

        // NEW: Unmount a game by title ID
        public async Task<(bool success, string message)> UnmountGameAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.UnmountGame, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Launch a game by title ID
        public async Task<(bool success, string message)> LaunchGameAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.LaunchGame, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Delete a screenshot and its matching thumbnail
        public async Task<(bool success, string message)> DeleteScreenshotAsync(string fullPath)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(fullPath + "\0");
                await SendCommandAsync(Command.DeleteScreenshot, data);
                var (response, respData) = await ReceiveResponseAsync();
                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: List all screenshots stored on the PS5
        public async Task<List<PS5Screenshot>> ListScreenshotsAsync()
        {
            var shots = new List<PS5Screenshot>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ListScreenshots);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) return shots;

                string body = Encoding.UTF8.GetString(data);
                if (body.StartsWith("NO_SCREENSHOTS")) return shots;

                foreach (var line in body.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;

                    long.TryParse(parts[2], out long size);
                    long.TryParse(parts[3], out long mtimeUnix);

                    shots.Add(new PS5Screenshot
                    {
                        FullPath = parts[0],
                        FileName = parts[1],
                        Size = size,
                        ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(mtimeUnix).LocalDateTime
                    });
                }
            }
            catch { }
            finally
            {
                _commandLock.Release();
            }
            return shots.OrderByDescending(s => s.ModifiedTime).ToList();
        }

        // NEW: Get icon0.png binary data for a game
        public async Task<byte[]?> GetGameIconAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.GetGameIcon, data);
                var (response, respData) = await ReceiveResponseAsync();
                if (response != Response.Data || respData == null || respData.Length == 0)
                    return null;
                return respData;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get pic0.png (picType=0) or pic1.png (picType=1) binary data for a game
        public async Task<byte[]?> GetGamePicAsync(string titleId, int picType)
        {
            if (picType != 0 && picType != 1) return null;

            await _commandLock.WaitAsync();
            try
            {
                string request = $"{titleId}:{picType}";
                byte[] data = Encoding.UTF8.GetBytes(request + "\0");
                await SendCommandAsync(Command.GetGamePic, data);
                var (response, respData) = await ReceiveResponseAsync();
                if (response != Response.Data || respData == null || respData.Length == 0)
                    return null;
                return respData;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get detailed info about a game
        public async Task<Dictionary<string, string>?> GetGameDetailsAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.GetGameDetails, data);
                var (response, respData) = await ReceiveResponseAsync();
                if (response != Response.Data) return null;

                var dict = new Dictionary<string, string>();
                string responseStr = Encoding.UTF8.GetString(respData);
                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    dict[key] = value;
                }
                return dict;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<FileEntry[]> ListDirAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> CreateDirAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.CreateDir, pathBytes);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> DeleteFileAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.DeleteFile, pathBytes);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public event Action<string>? OnProgressMessage;

        public async Task<bool> DeleteDirAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> RenameAsync(string oldPath, string newPath)
        {
            await _commandLock.WaitAsync();
            try
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> CopyFileAsync(string srcPath, string dstPath)
        {
            await _commandLock.WaitAsync();
            try
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> MoveFileAsync(string srcPath, string dstPath)
        {
            await _commandLock.WaitAsync();
            try
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> UploadFileAsync(string localPath, string remotePath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default, long chunkOffset = 0, long chunkSize = 0, Action? onReadyCallback = null)
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
                onReadyCallback?.Invoke(); // Signal even on failure so waiters don't deadlock
                return false;
            }
            
            // Signal that file is created/pre-allocated on PS5 (for parallel chunk synchronization)
            onReadyCallback?.Invoke();

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
                        
                        // FIX #6: Reduced timeout from 15 min to 3 min for faster failure detection
                        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            await _commandLock.WaitAsync();
            try
            {
                // Send download command (must include null terminator for C strlen())
                byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath + "\0");
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> OpenShellAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ShellOpen);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<string> ExecuteShellCommandAsync(string command)
        {
            await _commandLock.WaitAsync();
            try
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
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> CloseShellAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ShellClose);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Index methods
        public async Task<bool> StartIndexAsync(string paths)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(paths + "\0");
                await SendCommandAsync(Command.IndexStart, pathBytes);
                var (response, data) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<string> GetIndexStatusAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.IndexStatus);
                var (response, data) = await ReceiveResponseAsync();
                if (response == Response.Ok && data.Length > 0)
                {
                    return Encoding.UTF8.GetString(data).TrimEnd('\0');
                }
                return "Unknown";
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<SearchResult[]> SearchIndexAsync(string query)
        {
            await _commandLock.WaitAsync();
            try
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
                try { await ReadExactAsync(respBuf, 1); }
                catch { break; }
                
                Response response = (Response)respBuf[0];
                
                if (response == Response.Data)
                {
                    // Read raw data: path_len(4) + path + name_len(4) + name + size(8) + mtime(8) + is_dir(1)
                    byte[] pathLenBuf = new byte[4];
                    await ReadExactAsync(pathLenBuf, 4);
                    uint pathLen = BitConverter.ToUInt32(pathLenBuf, 0);
                    
                    byte[] pathBuf = new byte[pathLen];
                    await ReadExactAsync(pathBuf, (int)pathLen);
                    string path = Encoding.UTF8.GetString(pathBuf);
                    
                    byte[] nameLenBuf = new byte[4];
                    await ReadExactAsync(nameLenBuf, 4);
                    uint nameLen = BitConverter.ToUInt32(nameLenBuf, 0);
                    
                    byte[] nameBuf = new byte[nameLen];
                    await ReadExactAsync(nameBuf, (int)nameLen);
                    string name = Encoding.UTF8.GetString(nameBuf);
                    
                    byte[] sizeBuf = new byte[8];
                    await ReadExactAsync(sizeBuf, 8);
                    long size = BitConverter.ToInt64(sizeBuf, 0);
                    
                    byte[] mtimeBuf = new byte[8];
                    await ReadExactAsync(mtimeBuf, 8);
                    long mtime = BitConverter.ToInt64(mtimeBuf, 0);
                    
                    byte[] isDirBuf = new byte[1];
                    await ReadExactAsync(isDirBuf, 1);
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
                    await ReadExactAsync(dataLenBuf, 4);
                    uint dataLen = BitConverter.ToUInt32(dataLenBuf, 0);
                    if (dataLen > 0)
                    {
                        byte[] msgBuf = new byte[dataLen];
                        await ReadExactAsync(msgBuf, (int)dataLen);
                    }
                    break;
                }
                else if (response == Response.Error)
                {
                    // Read error message
                    byte[] dataLenBuf = new byte[4];
                    await ReadExactAsync(dataLenBuf, 4);
                    uint dataLen = BitConverter.ToUInt32(dataLenBuf, 0);
                    if (dataLen > 0)
                    {
                        byte[] msgBuf = new byte[dataLen];
                        await ReadExactAsync(msgBuf, (int)dataLen);
                    }
                    break;
                }
            }
            
            return results.ToArray();
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Mount all games on PS5 (scans /data/etaHEN/games, USB drives, M.2 SSD)
        // Response pattern: RESP_OK with summary text
        public async Task<string?> MountGamesAsync(CancellationToken cancellationToken = default)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.MountGames);
                
                var (response, data) = await ReceiveResponseAsync();
                
                if (response == Response.Ok && data != null)
                {
                    return Encoding.UTF8.GetString(data);
                }
                else if (response == Response.Error && data != null)
                {
                    return "ERROR: " + Encoding.UTF8.GetString(data);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] MountGamesAsync: {ex.Message}");
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
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

        /// <summary>
        /// Recursively download an entire folder from PS5 to local PC.
        /// Uses a separate connection for listing to avoid protocol desync.
        /// </summary>
        public async Task<(int filesDownloaded, int filesFailed, long totalBytes)> DownloadFolderAsync(
            string remotePath,
            string localBasePath,
            string ps5IpAddress,
            IProgress<DownloadFolderProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // Phase 1: Scan the remote folder tree to build a file list
            var filesToDownload = new System.Collections.Generic.List<(string remotePath, string localPath, long size)>();
            var dirsToCreate = new System.Collections.Generic.List<string>();

            await ScanRemoteFolderAsync(remotePath, localBasePath, remotePath, filesToDownload, dirsToCreate, ps5IpAddress, progress, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return (0, 0, 0);

            // Phase 2: Create all local directories
            foreach (var dir in dirsToCreate)
            {
                Directory.CreateDirectory(dir);
            }

            long totalBytes = 0;
            foreach (var f in filesToDownload) totalBytes += f.size;

            progress?.Report(new DownloadFolderProgress
            {
                Phase = "Downloading",
                CurrentFile = "",
                FilesCompleted = 0,
                TotalFiles = filesToDownload.Count,
                BytesDownloaded = 0,
                TotalBytes = totalBytes
            });

            // Phase 3: Download each file using a dedicated connection per file
            int filesDownloaded = 0;
            int filesFailed = 0;
            long bytesDownloaded = 0;

            for (int i = 0; i < filesToDownload.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var (rPath, lPath, fSize) = filesToDownload[i];
                string fileName = Path.GetFileName(lPath);

                progress?.Report(new DownloadFolderProgress
                {
                    Phase = "Downloading",
                    CurrentFile = fileName,
                    FilesCompleted = filesDownloaded,
                    TotalFiles = filesToDownload.Count,
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = totalBytes
                });

                // Use a fresh connection for each file to avoid protocol desync
                using var dlProto = new PS5Protocol();
                if (!await dlProto.ConnectAsync(ps5IpAddress))
                {
                    filesFailed++;
                    continue;
                }

                try
                {
                    bool ok = await dlProto.DownloadFileAsync(rPath, lPath, null, cancellationToken);
                    if (ok)
                    {
                        filesDownloaded++;
                        bytesDownloaded += fSize;
                    }
                    else
                    {
                        filesFailed++;
                    }
                }
                catch
                {
                    filesFailed++;
                }
            }

            progress?.Report(new DownloadFolderProgress
            {
                Phase = "Complete",
                CurrentFile = "",
                FilesCompleted = filesDownloaded,
                TotalFiles = filesToDownload.Count,
                BytesDownloaded = bytesDownloaded,
                TotalBytes = totalBytes
            });

            return (filesDownloaded, filesFailed, bytesDownloaded);
        }

        private async Task ScanRemoteFolderAsync(
            string currentRemotePath,
            string localBasePath,
            string remoteRootPath,
            System.Collections.Generic.List<(string remotePath, string localPath, long size)> files,
            System.Collections.Generic.List<string> dirs,
            string ps5IpAddress,
            IProgress<DownloadFolderProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return;

            // Calculate relative path for local directory
            string relativePath = currentRemotePath.Length > remoteRootPath.Length
                ? currentRemotePath.Substring(remoteRootPath.Length).TrimStart('/')
                : "";
            string localDir = string.IsNullOrEmpty(relativePath)
                ? localBasePath
                : Path.Combine(localBasePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            dirs.Add(localDir);

            progress?.Report(new DownloadFolderProgress
            {
                Phase = "Scanning",
                CurrentFile = currentRemotePath,
                FilesCompleted = files.Count,
                TotalFiles = 0,
                BytesDownloaded = 0,
                TotalBytes = 0
            });

            // Use a fresh connection for listing to avoid protocol desync
            FileEntry[] entries;
            using (var listProto = new PS5Protocol())
            {
                if (!await listProto.ConnectAsync(ps5IpAddress))
                    return;

                entries = await listProto.ListDirAsync(currentRemotePath);
            }

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (entry.Name == "." || entry.Name == "..") continue;

                string entryRemotePath = currentRemotePath.TrimEnd('/') + "/" + entry.Name;
                string entryLocalPath = Path.Combine(localDir, entry.Name);

                if (entry.IsDirectory)
                {
                    await ScanRemoteFolderAsync(entryRemotePath, localBasePath, remoteRootPath, files, dirs, ps5IpAddress, progress, cancellationToken);
                }
                else
                {
                    files.Add((entryRemotePath, entryLocalPath, entry.Size));
                }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    public class DownloadFolderProgress
    {
        public string Phase { get; set; } = "";
        public string CurrentFile { get; set; } = "";
        public int FilesCompleted { get; set; }
        public int TotalFiles { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
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

    // New data classes for enhanced features
    public class PS5FileInfo
    {
        public long Size { get; set; }
        public DateTime ModifiedTime { get; set; }
        public DateTime AccessTime { get; set; }
        public int Permissions { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsSymlink { get; set; }
    }

    public class PS5SystemInfo
    {
        public string Hostname { get; set; } = "PS5";
        public string ServerVersion { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public ulong TotalMemory { get; set; }
        public ulong StorageTotal { get; set; }
        public ulong StorageFree { get; set; }
        public int MountedGames { get; set; }
        public bool IndexReady { get; set; }
        public int IndexFiles { get; set; }
        public int IndexDirs { get; set; }
        public long ServerUptime { get; set; }
    }

    public class FileVerificationResult
    {
        public string CRC32 { get; set; } = "";
        public ulong Size { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }

    // Hardware info
    public class PS5HardwareInfo
    {
        public string Model { get; set; } = "";
        public string Serial { get; set; } = "";
        public bool HasWlanBt { get; set; }
        public bool HasOpticalOut { get; set; }
        public string HwMachine { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public int NumCpu { get; set; }
        public ulong PhysMem { get; set; }
    }

    // Temperature and CPU info
    public class PS5TemperatureInfo
    {
        public int CpuTemp { get; set; }
        public int SocTemp { get; set; }
        public long CpuFreqMhz { get; set; }
        public uint SocPowerMw { get; set; }
        public int[] CpuUsage { get; set; } = new int[8];
    }

    // Running app info
    public class PS5RunningApp
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string TitleId { get; set; } = "";
        public uint AppId { get; set; }
    }

    // Screenshot info (notifies UI when thumbnail loads)
    public class PS5Screenshot : System.ComponentModel.INotifyPropertyChanged
    {
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public DateTime ModifiedTime { get; set; }

        private System.Windows.Media.ImageSource? _thumbnail;
        public System.Windows.Media.ImageSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string SizeDisplay
        {
            get
            {
                if (Size < 1024) return $"{Size} B";
                if (Size < 1024 * 1024) return $"{Size / 1024.0:0.0} KB";
                return $"{Size / (1024.0 * 1024.0):0.0} MB";
            }
        }

        public string DateDisplay => ModifiedTime.ToString("yyyy-MM-dd HH:mm");
    }

    // Power info
    public class PS5PowerInfo
    {
        public ulong OperatingTimeSec { get; set; }
        public ulong OperatingTimeHours { get; set; }
        public ulong OperatingTimeMinutes { get; set; }
        public uint BootCount { get; set; }
        public uint PowerConsumptionMw { get; set; }
    }

    // Save game info
    public class PS5SaveGame : System.ComponentModel.INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string SavePath { get; set; } = "";
        public ulong Size { get; set; }
        public long ModifiedUnixTime { get; set; }

        // Display helpers
        public string SizeDisplay
        {
            get
            {
                double mb = Size / (1024.0 * 1024.0);
                if (mb < 1) return $"{Size / 1024.0:F1} KB";
                if (mb < 1024) return $"{mb:F1} MB";
                return $"{mb / 1024.0:F2} GB";
            }
        }

        public string ModifiedDisplay
        {
            get
            {
                try
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(ModifiedUnixTime).LocalDateTime;
                    return dt.ToString("yyyy-MM-dd HH:mm");
                }
                catch { return "—"; }
            }
        }

        public string ShortUserId => UserId.Length > 8 ? UserId.Substring(0, 8) + "…" : UserId;

        private string _gameName = "";
        public string GameName
        {
            get => _gameName;
            set { _gameName = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GameName))); }
        }

        private System.Windows.Media.ImageSource? _icon;
        public System.Windows.Media.ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // Mounted game info
    public class PS5MountedGame : System.ComponentModel.INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public ulong Size { get; set; }
        public string Region { get; set; } = "";
        public bool IsActive { get; set; }

        private System.Windows.Media.ImageSource? _icon;
        public System.Windows.Media.ImageSource? Icon
        {
            get => _icon;
            set
            {
                _icon = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public string SizeDisplay
        {
            get
            {
                double mb = Size / (1024.0 * 1024.0);
                if (mb < 1024) return $"{mb:F1} MB";
                return $"{mb / 1024.0:F2} GB";
            }
        }

        public string StatusDisplay => IsActive ? "✓ Mounted" : "✗ Not Mounted";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
