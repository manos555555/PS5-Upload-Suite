#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace PS5Upload
{
    // ========== Additional commands: Hardware, Games, Saves, Screenshots, Mount, Shell, etc. ==========
    public partial class PS5Protocol
    {
        // ---------------- Hardware info ----------------
        public async Task<PS5HardwareInfo?> GetHardwareInfoAsync()
        {
            try
            {
                var (response, data) = await RunCommandAsync(Command.GetHwInfo);
                if (response != Response.Data) return null;
                string text = Encoding.UTF8.GetString(data);
                var info = new PS5HardwareInfo();
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var k = parts[0].Trim(); var v = parts[1].Trim();
                    switch (k)
                    {
                        case "model": info.Model = v; break;
                        case "serial": info.Serial = v; break;
                        case "has_wlan_bt": info.HasWlanBt = v == "1"; break;
                        case "has_optical_out": info.HasOpticalOut = v == "1"; break;
                        case "hw_machine": info.HwMachine = v; break;
                        case "os": info.OsVersion = v; break;
                        case "ncpu": if (int.TryParse(v, out var n)) info.NumCpu = n; break;
                        case "physmem": if (ulong.TryParse(v, out var pm)) info.PhysMem = pm; break;
                    }
                }
                return info;
            }
            catch { return null; }
        }

        // ---------------- Live sensors ----------------
        public async Task<PS5TemperatureInfo?> GetTemperatureInfoAsync()
        {
            try
            {
                var (response, data) = await RunCommandAsync(Command.GetTemps);
                if (response != Response.Data) return null;
                string text = Encoding.UTF8.GetString(data);
                var info = new PS5TemperatureInfo();
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var k = parts[0].Trim(); var v = parts[1].Trim();
                    switch (k)
                    {
                        case "cpu_temp": if (int.TryParse(v, out var ct)) info.CpuTemp = ct; break;
                        case "soc_temp": if (int.TryParse(v, out var st)) info.SocTemp = st; break;
                        case "cpu_freq_mhz": if (long.TryParse(v, out var cf)) info.CpuFreqMhz = cf; break;
                        case "soc_power_mw": if (uint.TryParse(v, out var pw)) info.SocPowerMw = pw; break;
                    }
                }
                return info;
            }
            catch { return null; }
        }

        // ---------------- Power ----------------
        public async Task<PS5PowerInfo?> GetPowerInfoAsync()
        {
            try
            {
                var (response, data) = await RunCommandAsync(Command.GetPowerInfo);
                if (response != Response.Data) return null;
                string text = Encoding.UTF8.GetString(data);
                var info = new PS5PowerInfo();
                foreach (var line in text.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var k = parts[0].Trim(); var v = parts[1].Trim();
                    switch (k)
                    {
                        case "operating_time_sec": if (ulong.TryParse(v, out var a)) info.OperatingTimeSec = a; break;
                        case "operating_time_hours": if (ulong.TryParse(v, out var b)) info.OperatingTimeHours = b; break;
                        case "operating_time_minutes": if (ulong.TryParse(v, out var c)) info.OperatingTimeMinutes = c; break;
                        case "boot_count": if (uint.TryParse(v, out var d)) info.BootCount = d; break;
                        case "power_consumption_mw": if (uint.TryParse(v, out var e)) info.PowerConsumptionMw = e; break;
                    }
                }
                return info;
            }
            catch { return null; }
        }

        // ---------------- Mounted games ----------------
        public async Task<List<PS5MountedGame>> GetGameListAsync()
        {
            var result = new List<PS5MountedGame>();
            try
            {
                var (response, data) = await RunCommandAsync(Command.GetGameList);
                if (response != Response.Data) return result;
                string text = Encoding.UTF8.GetString(data);
                if (text.Trim() == "NO_GAMES") return result;
                foreach (var line in text.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 6) continue;
                    var g = new PS5MountedGame
                    {
                        TitleId = parts[0],
                        Name = parts[1],
                        Path = parts[2],
                        Region = parts[4],
                        IsActive = parts[5] == "1"
                    };
                    if (ulong.TryParse(parts[3], out var sz)) g.Size = sz;
                    result.Add(g);
                }
            }
            catch { }
            return result;
        }

        public async Task<(bool success, string message)> UnmountGameAsync(string titleId)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(titleId + "\0");
                var (response, respData) = await RunCommandAsync(Command.UnmountGame, data);
                return (response == Response.Ok, Encoding.UTF8.GetString(respData));
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public async Task<(bool success, string message)> LaunchGameAsync(string titleId)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(titleId + "\0");
                var (response, respData) = await RunCommandAsync(Command.LaunchGame, data);
                return (response == Response.Ok, Encoding.UTF8.GetString(respData));
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public async Task<byte[]?> GetGameIconAsync(string titleId)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(titleId + "\0");
                var (response, respData) = await RunCommandAsync(Command.GetGameIcon, data);
                if (response != Response.Data || respData == null || respData.Length == 0) return null;
                return respData;
            }
            catch { return null; }
        }

        public async Task<Dictionary<string, string>?> GetGameDetailsAsync(string titleId)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(titleId + "\0");
                var (response, respData) = await RunCommandAsync(Command.GetGameDetails, data);
                if (response != Response.Data) return null;
                var dict = new Dictionary<string, string>();
                foreach (var line in Encoding.UTF8.GetString(respData).Split('\n'))
                {
                    var p = line.Split('=', 2);
                    if (p.Length == 2) dict[p[0].Trim()] = p[1].Trim();
                }
                return dict;
            }
            catch { return null; }
        }

        // ---------------- Saves ----------------
        public async Task<List<PS5SaveGame>> ListSavesAsync()
        {
            var saves = new List<PS5SaveGame>();
            try
            {
                var (response, data) = await RunCommandAsync(Command.ListSaves);
                if (response != Response.Data) return saves;
                string text = Encoding.UTF8.GetString(data);
                if (text.Trim() == "NO_SAVES") return saves;
                foreach (var line in text.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 5) continue;
                    var s = new PS5SaveGame
                    {
                        TitleId = parts[0],
                        UserId = parts[1],
                        SavePath = parts[2]
                    };
                    if (ulong.TryParse(parts[3], out var sz)) s.Size = sz;
                    if (long.TryParse(parts[4], out var mt)) s.ModifiedUnixTime = mt;
                    saves.Add(s);
                }
            }
            catch { }
            return saves;
        }

        // ---------------- Screenshots ----------------
        public async Task<List<PS5Screenshot>> ListScreenshotsAsync()
        {
            var shots = new List<PS5Screenshot>();
            try
            {
                var (response, data) = await RunCommandAsync(Command.ListScreenshots);
                if (response != Response.Data) return shots;
                string body = Encoding.UTF8.GetString(data);
                if (body.StartsWith("NO_SCREENSHOTS")) return shots;
                foreach (var line in body.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    long.TryParse(parts[2], out var size);
                    long.TryParse(parts[3], out var mtime);
                    shots.Add(new PS5Screenshot
                    {
                        FullPath = parts[0],
                        FileName = parts[1],
                        Size = size,
                        ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime
                    });
                }
            }
            catch { }
            return shots.OrderByDescending(s => s.ModifiedTime).ToList();
        }

        public async Task<(bool success, string message)> DeleteScreenshotAsync(string fullPath)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(fullPath + "\0");
                var (response, respData) = await RunCommandAsync(Command.DeleteScreenshot, data);
                return (response == Response.Ok, Encoding.UTF8.GetString(respData));
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ---------------- Mount all games (long-running, progress) ----------------
        public async Task<(bool success, string summary)> MountGamesAsync(Action<string>? onProgress = null)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandInternalAsync(Command.MountGames);
                // Server streams Progress responses, then final OK or Error
                while (true)
                {
                    var (resp, data) = await ReceiveResponseInternalAsync();
                    if (resp == Response.Progress)
                    {
                        onProgress?.Invoke(Encoding.UTF8.GetString(data).TrimEnd('\0'));
                    }
                    else if (resp == Response.Ok)
                    {
                        return (true, Encoding.UTF8.GetString(data));
                    }
                    else if (resp == Response.Error)
                    {
                        return (false, Encoding.UTF8.GetString(data));
                    }
                    else { return (false, $"Unexpected response {resp}"); }
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { _commandLock.Release(); }
        }

        // ---------------- Launch Browser ----------------
        public async Task<(bool success, string message)> LaunchBrowserAsync(string url)
        {
            try
            {
                var data = Encoding.UTF8.GetBytes(url + "\0");
                var (response, respData) = await RunCommandAsync(Command.LaunchBrowser, data);
                return (response == Response.Ok, Encoding.UTF8.GetString(respData));
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // Shell methods (OpenShellAsync/ExecuteShellCommandAsync/CloseShellAsync)
        // already exist in Protocol.cs — not duplicated here.
    }

    // ========== Data models ==========

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

        public string PhysMemDisplay
        {
            get
            {
                if (PhysMem == 0) return "—";
                double gb = PhysMem / (1024.0 * 1024.0 * 1024.0);
                return $"{gb:F1} GB";
            }
        }

        public string WlanBtDisplay => HasWlanBt ? "✓ Yes" : "✗ None";
        public string OpticalDisplay => HasOpticalOut ? "✓ Yes" : "✗ None";
    }

    public class PS5TemperatureInfo
    {
        public int CpuTemp { get; set; }
        public int SocTemp { get; set; }
        public long CpuFreqMhz { get; set; }
        public uint SocPowerMw { get; set; }

        public string CpuTempDisplay => CpuTemp > 0 ? $"{CpuTemp}°C" : "—";
        public string SocTempDisplay => SocTemp > 0 ? $"{SocTemp}°C" : "—";
        public string CpuFreqDisplay => CpuFreqMhz > 0 ? $"{CpuFreqMhz / 1000.0:F2} GHz" : "—";
        public string SocPowerDisplay => SocPowerMw > 0 ? $"{SocPowerMw / 1000.0:F1} W" : "—";
    }

    public class PS5PowerInfo
    {
        public ulong OperatingTimeSec { get; set; }
        public ulong OperatingTimeHours { get; set; }
        public ulong OperatingTimeMinutes { get; set; }
        public uint BootCount { get; set; }
        public uint PowerConsumptionMw { get; set; }
    }

    public class PS5MountedGame : INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public ulong Size { get; set; }
        public string Region { get; set; } = "";
        public bool IsActive { get; set; }

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
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

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class PS5SaveGame : INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string SavePath { get; set; } = "";
        public ulong Size { get; set; }
        public long ModifiedUnixTime { get; set; }

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
            set { _gameName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GameName))); }
        }

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class PS5Screenshot : INotifyPropertyChanged
    {
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public DateTime ModifiedTime { get; set; }

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

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
}
