# 🚀 PS5 Upload Suite v5.0.0 — Complete Management Suite

Complete all-in-one PS5 management platform — **104+ MB/s** file transfers, live hardware monitoring, full game/save/screenshot management, and deep PS5 integration through a single desktop client + a robust PS5 payload.

---

## 📦 Downloads

| File | Size | Purpose |
|------|------|---------|
| `PS5Upload.exe` | 146 MB | Windows GUI client (self-contained, no .NET install required) |
| `ps5_upload_server.elf` | 187 KB | PS5 payload — load via elfldr, listens on port 9113 |

**Load the payload on your PS5:** copy `ps5_upload_server.elf` to `/data/etaHEN/payloads/` and run it. You will see a notification `PS5 Upload Server: 192.168.0.XXX:9113 - By Manos` confirming it's ready.

---

## 🆕 What's New in v5.0.0

### 🖥️ NEW: Hardware Monitor Tab
- **System Info** — Model (CFI-xxxx), Serial Number, Architecture, OS version
- **Physical RAM detection** — 5-step fallback chain (PS5-specific API → sysctl chain → 16 GB default)
- **Live Sensors** — CPU temperature, SoC temperature, CPU frequency, SoC power consumption
- **Auto-refresh** — Configurable 5-second polling with automatic disconnect detection
- **Payload hardened** — `pthread_mutex_t` + 1s cache for sensors, 5s throttle for power API, rejection of abnormal readings

### 📷 NEW: Screenshots Tab
- **Visual Gallery** — Live thumbnail previews (200px downsampled for fast loading)
- **Smart Cache** — Local cache in `%TEMP%` keyed by remote path hash — zero re-downloads on refresh
- **Batch Download** — Multi-select and download multiple screenshots
- **Clean Delete** — Removes ALL 3 files per screenshot (`.dat`, `.meta`, `.jpg.jpeg`) — **no more ghost entries in PS5 Media Gallery**
- **Double-click Preview** — Opens in default image viewer
- **Full Path Scanning** — 5-level recursive scan of `/user/av_contents/thumbnails/photo/…`

### ▶️ NEW: Launch Game
- Right-click any mounted game → **Launch Game**
- Uses low-level `sceLncUtilLaunchApp` with fallback to `sceSystemServiceLaunchApp`
- **Works on pirated/sideloaded games** (bypasses `0x80940005` permission error)
- Triple-strategy launcher with detailed diagnostic error messages

### 💾 NEW: Save Manager Tab
- Per-game save browsing with metadata
- Game icons fetched and cached from PSN/param.sfo
- Batch download saves to local backup folder
- Upload saves back to PS5
- Size, date, and title ID display

### 🎮 NEW: Game Details Window
- **PSN Cover Art** fetched from PlayStation Store
- Full `param.json` metadata formatted nicely
- Online PSN info: description, genres, age rating, publisher
- Alternate title ID detection (regional variants)
- Browser search fallback if API fails

### 🔧 Improved Storage Reporting
- ✅ Matches **PS5 Settings "Console Storage"** formula exactly
- ✅ Total: `/user` effective + `/system_data` + `/system_ex`
- ✅ Free: proper `f_bavail` across all partitions
- ✅ Accounts for reserved space & per-partition blocksize
- Example real-world PS5: **848 GB total / 354 GB free** ✓

### 🎮 Improved Mount Games
- ✅ Correct `sceAppInstUtilAppInstallTitleDir(title_id, "/user/app/", 0)` signature
- ✅ Registration BEFORE `mount.lnk` (correct order)
- ✅ Direct in-payload registration — no external `game_mounter.elf` dependency
- ✅ Proper DRM patching & metadata copy

### 🛡️ Stability Overhaul
- ✅ **Thread-safe sensor reads** — `pthread_mutex_t` prevents concurrent API crashes
- ✅ **API call throttling** — SoC power read at most every 5s, sensor cache every 1s
- ✅ **Cached static HW info** — Read once, serve forever (Model, Serial, OS)
- ✅ **Unsafe APIs removed** — `sceKernelIccGet*`, `HwHasWlanBt`, `HwHasOpticalOut` (caused crashes)
- ✅ **Client busy-flag guard** — `Interlocked` prevents overlapping refreshes
- ✅ **Auto-stop timers** — Hardware auto-refresh stops on disconnect
- ✅ **Graceful reconnect** — Connection lost → automatic reconnection attempt

---

## 🐛 Bug Fixes

- **Launch failed with 0x80940005** — Fixed by using low-level `sceLncUtilLaunchApp` instead of `sceSystemServiceLaunchApp` for pirated/mounted games
- **Payload crashes on Hardware Tab refresh** — Fixed with mutex, caching, and removal of unsafe `sceKernelIccGet*` APIs
- **Connection freezes after auto-refresh enabled** — Fixed with busy-flag guard, auto-stop on disconnect, and longer polling interval
- **Ghost screenshots in PS5 Media Gallery after delete** — Fixed by deleting all 3 associated files (`.dat`, `.meta`, `.jpg.jpeg`)
- **Physical RAM showing "—"** — Fixed with 5-step detection chain including PS5-specific `sceKernelGetDirectMemorySize()`
- **Wi-Fi / Bluetooth showing "✗ None"** — Fixed (hardcoded to `true` since all PS5 models have it)
- **Screenshots showing 0 items** — Fixed by scanning correct path `/user/av_contents/thumbnails/photo/` (where viewable `.jpg.jpeg` files live)
- **Storage reporting mismatch with PS5 Settings** — Fixed with correct aggregation formula

---

## 🔧 Technical Details

### New Payload Commands
- `CMD_LAUNCH_GAME (0x44)` — Launch mounted games
- `CMD_LIST_SCREENSHOTS (0x45)` — Recursive scan of screenshot directories
- `CMD_DELETE_SCREENSHOT (0x46)` — Clean delete (original + thumbnail + metadata)

### New Sony Kernel APIs Used (verified safe)
- `sceKernelGetHwModelName` / `sceKernelGetHwSerialNumber`
- `sceKernelGetCpuTemperature` / `sceKernelGetSocSensorTemperature`
- `sceKernelGetCpuFrequency`
- `sceKernelGetDirectMemorySize`
- `sceKernelGetSocPowerConsumption` (throttled to 5s)
- `sceLncUtilLaunchApp` / `sceLncUtilInitialize`
- `sceUserServiceInitialize` / `sceUserServiceGetForegroundUser`

### Removed APIs (destabilize payload)
- `sceKernelIccGetPowerOperatingTime`
- `sceKernelIccGetPowerNumberOfBootShutdown`
- `sceKernelHwHasWlanBt` / `sceKernelHwHasOpticalOut`
- `sceShellCoreUtilGetEffectiveTotalSizeOfUserPartition` (privileges issue)

---

## 🚀 Quick Start

1. **Load payload on PS5:** Copy `ps5_upload_server.elf` to `/data/etaHEN/payloads/` and execute it
2. **Run client on PC:** Launch `PS5Upload.exe` on Windows
3. **Connect:** Enter your PS5 IP (e.g. `192.168.0.160`) and click Connect
4. **Enjoy:** 8 tabs of full PS5 management — uploads, games, saves, screenshots, live hardware monitoring, and more!

---

## 📋 Full Feature List

See the [README](https://github.com/manos555555/PS5-Upload-Suite/blob/main/README.md) for the complete feature matrix.

---

## 🙏 Credits

- PS5 SDK by John Törnblom
- etaHEN team for the kernel runtime
- PS5 homebrew community
- Inspired by `ps5upload` by PhantomPtr

---

**Tested on:** PS5 Slim (CFI-2016 A01Y), firmware with etaHEN — 16 GB RAM, 16 CPU cores, ~848 GB storage

🎮 **Enjoy the complete PS5 management experience!**
