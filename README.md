# 🚀 PS5 Upload Suite - Complete PS5 Management Platform

**By Manos**

**Version 6.0.0 - Cross-Platform Release**

Complete all-in-one PS5 management platform with **104+ MB/s** high-speed file transfers, live hardware monitoring, full game/save/screenshot management, and deep PS5 integration — now running natively on **Windows, Linux, macOS** (Intel & Apple Silicon) and **Android**.

![PS5 Upload Suite v6.0 - Cross-Platform Desktop Client](screenshots/screenshot_v6.png)

---

⭐ **NEW in v6.0.0 (Cross-Platform Release):**

### 🌍 Cross-Platform Support
- **Windows x64**, **Linux x64**, **Linux ARM64**, **macOS Intel**, **macOS Apple Silicon**
- **Android 5.0+** companion app (`.apk`) — manage your PS5 from your phone/tablet
- Desktop builds are self-contained, single-file executables — no .NET runtime needed
- Desktop client built on **Avalonia UI** (full XAML rewrite from WPF)
- Mobile app built on **.NET MAUI**

### 📥 Downloads
| Platform | File |
|---|---|
| Windows 64-bit | `PS5UploadSuite-v6.0.0-Windows-x64.exe` |
| Linux 64-bit | `PS5UploadSuite-v6.0.0-Linux-x64` |
| Linux ARM64 (RPi etc.) | `PS5UploadSuite-v6.0.0-Linux-arm64` |
| macOS Intel | `PS5UploadSuite-v6.0.0-macOS-x64` |
| macOS Apple Silicon (M1+) | `PS5UploadSuite-v6.0.0-macOS-arm64` |
| Android 5.0+ | `PS5UploadSuite-v6.0.0-Android.apk` |
| PS5 Payload | `ps5_upload_server.elf` |

➡️ **[Get the latest release here](https://github.com/manos555555/PS5-Upload-Suite/releases/latest)**

### 🎨 Complete UI Rebuild
- Modern, native-feeling UI on every platform
- Faster startup and lower memory usage than the WPF version
- Properly themed dark mode on every OS

### 🐛 Major UI Fixes
- ✅ Fixed Auto-send Payload Expander overlap
- ✅ Fixed clipped tab toolbars (Screenshots, Saves, Games, Hardware)
- ✅ Fixed ComboBox dropdowns being cut by parent panels
- ✅ Fixed Transfer History expanders not stretching
- ✅ Fixed Storage Info layout on narrow windows
- ✅ App icon and header logo now properly displayed

### 📦 Payload
- Updated startup notification to **v6.0** (was incorrectly v3.0 in v5.0.0)
- No protocol or behavioral changes — fully compatible with previous installs

---

⭐ **v5.0.0 Features (carried over):**

### 🖥️ **NEW: Hardware Monitor Tab**
- 📋 **System Info** — Model (CFI-xxxx), Serial Number, Architecture, OS version
- 💾 **Physical RAM** — With 5-step detection fallback (PS5-specific API + sysctl chain + 16 GB default)
- 🔥 **Live Sensors** — CPU temperature, SoC temperature, CPU frequency, SoC power consumption
- 🔄 **Auto-refresh** — Configurable 5-second polling with automatic disconnect detection
- 🛡️ **Payload Hardened** — Mutex + 1s cache for sensors, 5s throttle for power API, rejection of abnormal readings

### 📷 **NEW: Screenshots Tab**
- 🖼️ **Visual Gallery** — Live thumbnail previews (200px downsampled for fast loading)
- 💾 **Smart Cache** — Local cache in `%TEMP%` keyed by remote path hash — zero re-downloads on refresh
- ⬇️ **Batch Download** — Multi-select and download multiple screenshots
- 🗑️ **Clean Delete** — Removes ALL 3 files per screenshot (`.dat`, `.meta`, `.jpg.jpeg`) — no more ghost entries in PS5 gallery
- 👁️ **Double-click Preview** — Opens in default image viewer
- 🔍 **Full Path Scanning** — 5-level recursive scan of `/user/av_contents/thumbnails/photo/…`

### ▶️ **NEW: Launch Game**
- Right-click any mounted game → **Launch Game**
- Uses `sceLncUtilLaunchApp` (low-level) with fallback to `sceSystemServiceLaunchApp`
- Works on mounted/sideloaded games (bypasses `0x80940005` permission error)
- Triple-strategy launcher with detailed diagnostic error messages

### 💾 **NEW: Save Manager Tab**
- **Full Save Browsing** — Per-game save data with metadata
- **Game Icons** — Fetched from PSN/local param.sfo (cached)
- **Download Saves** — Batch-download multiple saves to local folder
- **Upload Saves** — Drag & drop save files back to PS5
- **Save Details** — Size, modification time, title ID, path

### 🎮 **NEW: Game Details Window**
- **PSN Cover Art** — Official game covers fetched from PlayStation Store
- **Full Metadata** — param.json contents formatted nicely
- **Online PSN Info** — Description, genres, age rating, publisher
- **Alternate Title ID detection** — Auto-fallback for regional variants
- **Browser Search Fallback** — Opens PSN store search if API fails

### 🔧 **Improved Storage Reporting**
- ✅ Matches **PS5 Settings "Console Storage"** formula exactly
- ✅ Total: `/user` effective + `/system_data` + `/system_ex`
- ✅ Free: proper `f_bavail` across all partitions
- ✅ Accounts for reserved space & per-partition blocksize
- Example real-world PS5: **848 GB total / 354 GB free** ✓

### 🎮 **Improved Mount Games**
- ✅ Correct `sceAppInstUtilAppInstallTitleDir(title_id, "/user/app/", 0)` signature
- ✅ Registration BEFORE `mount.lnk` (correct order)
- ✅ Direct in-payload registration (no external `game_mounter.elf` dependency)
- ✅ Proper DRM patching & metadata copy

### 🛡️ **Stability Overhaul**
- ✅ **Thread-safe sensor reads** — `pthread_mutex_t` prevents concurrent API crashes
- ✅ **API call throttling** — SoC power read at most every 5s, sensor cache every 1s
- ✅ **Cached static HW info** — Read once, serve forever (Model, Serial, OS)
- ✅ **Unsafe APIs removed** — `sceKernelIccGet*`, `HwHasWlanBt`, `HwHasOpticalOut` (caused crashes)
- ✅ **Client busy-flag guard** — `Interlocked` prevents overlapping refreshes
- ✅ **Auto-stop timers** — Hardware auto-refresh stops on disconnect
- ✅ **Graceful reconnect** — Connection lost → automatic reconnection attempt

---

⭐ **v4.2.2 (Stability & Mount Fix):**
- 🎮 **Fixed Mount Games** - Games now properly appear on PS5 home screen after mounting
- 🗑️ **Fixed Unmount Games** - Games now properly removed from PS5 home screen when unmounted
- 🖥️ **Fixed Hardware Tab** - Added ScrollViewer for proper scrolling
- 🔧 **Fixed sceAppInstUtil calls** - Correct API usage with proper arguments
- ⚠️ **Disabled auto hardware refresh** - Prevents payload crashes (manual refresh still available)
- 🛡️ **Improved stability** - Fixed delayed payload crashes during folder navigation

⭐ **v4.2.1 (Bugfix):**
- 🖥️ **Fixed UI scrolling** - Connection panel no longer eats half the screen
- 🔢 **Fixed file counting** - Chunked files now count correctly as completed
- 📊 **Fixed frozen progress** - Progress updates no longer freeze during heavy uploads

⭐ **v4.2.0 Features:** 
- 🎮 **Mount Games Button** - Mount uploaded games directly from the client
- 🔍 **Multi-Path Scanning** - Scans internal storage, USB drives (0-3), and M.2 SSD
- 🔄 **Duplicate Detection** - Skips already-mounted games automatically
- 📊 **Mount Summary** - Detailed results with PS5 notifications
- 🧹 **Auto-Cleanup** - Removes mount entries for deleted games

📱 **Android Mobile Client available!**

---

## 📦 Downloads

Download the latest release from the [Releases](https://github.com/manos555555/PS5-Upload-Suite/releases) page:

### 1. PS5 Server Payload
- **File:** `ps5_upload_server.elf`
- **Port:** 9113
- High-speed custom binary protocol
- 16MB socket buffers for maximum throughput
- Multi-threaded client handling

### 2. Windows GUI Client
- **File:** `PS5Upload.exe`
- Self-contained executable (no .NET installation required, **~147 MB**)
- Modern dark theme UI with multi-tab layout
- **Tabs:** Debug Log, Transfer History, Shell, Search, Saves, Games, Screenshots, **Hardware 🆕**
- Drag & drop file/folder upload
- Real-time upload progress with sliding window speed tracking
- Transfer History with success/failed tracking and retry
- PS5 storage display (free/used/total — matches PS5 Settings)
- Game mounting with one-click Mount Games button
- **NEW:** Launch any mounted game with one click
- **NEW:** Live hardware monitor (CPU/SoC temps, frequency, power)
- **NEW:** Screenshot gallery with thumbnail previews and batch download/delete
- **NEW:** Save Manager with game icons and PSN metadata

### 3. 📱 Android Mobile Client
- **File:** `PS5UploadMobile.apk`
- Upload/download files from your phone
- Multi-PS5 Profiles support
- Browse PS5 filesystem
- Favorites for quick navigation

---

## 🚀 Quick Start

### Step 1: Load PS5 Payload

1. Download `ps5_upload_server.elf` from the [Releases](https://github.com/manos555555/PS5-Upload-Suite/releases) page

2. Copy it to your PS5: `/data/etaHEN/payloads/ps5_upload_server.elf`

3. Load the payload with elfldr

4. You should see notification: `PS5 Upload Server: 192.168.0.XXX:9113 - By Manos`

### Step 2: Run Windows Client

1. Run `PS5Upload.exe` on your PC

2. Enter your PS5's IP address (e.g., `192.168.0.160`)

3. Click **Connect**

4. Browse files/folders or drag & drop

5. Click **Upload to PS5**

---

## 🎨 Features

### PS5 Server Payload
✅ **High-Speed Protocol** - Custom binary protocol for maximum speed  
✅ **8MB Buffer** - Large buffer for high throughput  
✅ **16MB Socket Buffers** - Maximum network throughput  
✅ **Direct Disk I/O** - No temp files, direct write syscalls  
✅ **Multi-threaded** - Handle multiple clients simultaneously  
✅ **Per-File Mutex** - Safe parallel uploads without corruption  
✅ **Thread-safe Sensor Reads** - `pthread_mutex_t` around all kernel API calls  
✅ **Smart Caching** - 1s sensor cache, 5s power-API throttle, permanent HW info cache  
✅ **5-Minute Socket Timeout** - No connection drops on large files  
✅ **Game Mounting** - nullfs mount, DRM patching, metadata copy, `sceAppInstUtil` registration  
✅ **Game Launching** - `sceLncUtilLaunchApp` + fallback chain for pirated/mounted games  
✅ **Shell Commands** - Built-in shell with ls, cd, cat, mkdir, rm, etc.  
✅ **Filesystem Indexing** - In-memory index for instant search  
✅ **Screenshot Management** - Full scan + safe delete of PS5 media files (.dat + .meta + .jpg.jpeg)  
✅ **Hardware APIs** - Model, serial, temps, CPU freq, power via verified Sony kernel APIs  
✅ **Storage Matching PS5 Settings** - Aggregates /user + /system_data + /system_ex  

### Windows Client
✅ **Modern UI** - Dark theme, maximized window, 8+ functional tabs  
✅ **Drag & Drop** - Files and folders  
✅ **Browse PS5** - Navigate filesystem with favorites  
✅ **Real-time Speed** - Sliding window algorithm for accurate speed display  
✅ **Parallel Uploads** - 4 parallel chunked connections for 104+ MB/s  
✅ **Transfer History** - Track all uploads/downloads with success/failed status  
✅ **Retry Failed Transfers** - Right-click to retry failed uploads  
✅ **Duplicate File Dialog** - Handle file conflicts during upload  
✅ **Folder Upload** - Recursive directory upload  
✅ **Download Files** - Download from PS5 to PC  
✅ **💾 Storage Display** - Real-time PS5 storage info (matches PS5 Settings exactly)  
✅ **🔍 Smart Search** - Full filesystem indexing with instant search  
  - Wildcard support: `*.pkg`, `*loader*`, `game*.bin`
  - Size filters: `size:>1GB`, `size:<100MB`
  - Case-insensitive matching
  - Search both filename and full path
  - Double-click to navigate to folder

✅ **💻 Shell Terminal** - Execute commands directly on PS5  
  - Run system commands remotely
  - Real-time output display
  - Command history
  - Working directory support

✅ **⭐ Favorites/Bookmarks** - Quick navigation to saved paths  
✅ **👥 Multi-PS5 Profiles** - Save and switch between multiple PS5 consoles  

✅ **🎮 Mount Games** - Mount uploaded games directly from the client  
  - Scans all game paths: internal, USB drives (0-3), M.2 SSD
  - Duplicate detection across storage locations
  - Auto-cleanup of deleted game mounts
  - PS5 notifications with mount progress
  - In-payload registration (no external binary required)

✅ **▶️ Launch Games** - One-click launch for mounted games  
  - `sceLncUtilLaunchApp` low-level launcher
  - Works on pirated/sideloaded games
  - Fallback to SystemService API
  - Right-click context menu on any game

✅ **💾 Save Manager Tab** - Full PS5 save file management  
  - Per-game save browsing with thumbnails
  - Game icons fetched and cached from PSN/param.sfo
  - Batch download saves to local backup folder
  - Upload saves back to PS5
  - Size, date, and metadata display

✅ **🎮 Game Details Window** - Rich game information  
  - PSN cover art (fetched from PlayStation Store)
  - Full param.json metadata formatted
  - Online PSN description, genres, age rating, publisher
  - Alternate title ID detection (regional variants)
  - Browser search fallback

✅ **📷 Screenshots Tab** - Complete media gallery  
  - Visual thumbnail previews (downsampled 200px)
  - Local cache in %TEMP% (reuses across refreshes)
  - Batch download/delete multi-select
  - Double-click preview in default image viewer
  - Clean delete removes ALL 3 PS5 files (.dat + .meta + .jpg.jpeg)
  - No more ghost entries in PS5 Media Gallery

✅ **🖥️ Hardware Monitor Tab** - Live system stats  
  - **System Info:** Model (CFI-xxxx), Serial, Architecture, OS, CPU Cores, RAM, Wi-Fi/BT
  - **Live Sensors:** CPU temp, SoC temp, CPU frequency, SoC power consumption
  - Auto-refresh with configurable 5s interval
  - Busy-flag guard prevents overlapping refreshes
  - Auto-stops on disconnect
  - Progress bars for visual feedback

---

## 📊 Performance

### v3.1.0 Optimized Results:
- ✅ **104+ MB/s** upload speed for large files (parallel chunked uploads)
- ✅ **4 parallel chunk connections** for maximum throughput
- ✅ **500MB chunks** - optimal size for PS5 disk performance
- ✅ **Real-time progress** - Speed/ETA updates during upload
- ✅ **Zero chunk failures** - Fixed race conditions
- ✅ **Fully responsive UI** throughout upload
- ✅ **No memory leaks** - stable operation

### v4.1.0 Real-World Test (Astrobot):
- ✅ **156,253 files** uploaded successfully in **53 minutes**
- ✅ **1,724 directories** created
- ✅ **Zero crashes** - stable memory usage throughout
- ✅ **Smooth UI** - no stuttering with massive file counts

### Previous Results (v1.3.0):
- ✅ **42,801 files** uploaded successfully
- ✅ **60-150 MB/s** sustained throughput

| Network | Expected Upload Speed | Expected Download Speed |
|---------|----------------------|------------------------|
| **Gigabit Ethernet** | 88-110 MB/s (aggregate) | 100-120 MB/s |
| **WiFi 6 (5GHz)** | 32-70 MB/s (aggregate) | 60-80 MB/s |
| **WiFi 5 (5GHz)** | 20-50 MB/s (aggregate) | 40-60 MB/s |

**Note:** Upload speeds depend on network quality and PS5 disk write performance. WiFi has ~40-60% overhead compared to Ethernet.


---

## 🛠️ Troubleshooting

### Connection Failed
- Make sure PS5 payload is running
- Check PS5 IP address is correct
- Verify both devices are on same network
- Check firewall settings

### Slow Upload Speed
- Use wired Ethernet connection (not WiFi)
- Close other network applications
- Check PS5 disk health

### Upload Fails
- Check PS5 has enough free space
- Verify destination path exists
- Check file permissions

---

## 🔒 Security Notes

- Server only accepts connections from local network
- No authentication required (local network only)
- SHUTDOWN command only works from localhost

---

## 📝 What's New in v4.2.0

### 🎮 Game Mounter Integration
- **Mount Games Button** - New green "🎮 Mount Games" button in the client UI
- **Multi-Path Scanning** - Scans 6 locations: `/data/etaHEN/games`, USB drives (0-3), M.2 SSD (`/mnt/ext0/games`)
- **Duplicate Detection** - If the same game exists on multiple drives, it's mounted only once
- **Auto-Cleanup** - Automatically unmounts entries for games that have been deleted
- **PS5 Notifications** - Real-time progress notifications on PS5 screen during mounting
- **Detailed Summary** - Shows new mounts, already mounted, duplicates skipped, and failures
- **Auto Registration** - Automatically runs `game_mounter.elf` for home screen registration when new games are detected

### 🔧 Payload Improvements
- **New Protocol Command** - `CMD_MOUNT_GAMES` (0x30) for triggering game mount from client
- **nullfs Mounting** - Games mounted via nullfs to `/system_ex/app/`
- **DRM Patching** - Automatic DRM type fix in param.json
- **sce_sys Copy** - Copies game metadata to `/user/app/` and `/user/appmeta/`
- **mount.lnk Tracking** - Creates mount link files for persistent mount state

### ⚡ Client Improvements
- **Real-Time Speed Display** - Sliding window algorithm for accurate real-time upload speed
- **Smoother ETA** - Blended speed calculation (60% real-time + 40% average) with better formatting
- **Duplicate File Dialog** - New dialog for handling file conflicts during upload
- **Move Dialog Refactor** - Reusable `ShowPathPickerDialogAsync()` replaces inline code

### 🐛 Bug Fixes
- **Fixed IOVEC_ENTRY(NULL) crash** - `strlen(NULL)` with `-O3` optimization caused payload crash
- **Fixed libSceAppInstUtil crash** - Library not available in etaHEN payload context, switched to standalone registration
- **Removed system() calls** - Replaced `system("rm -rf ...")` with safe recursive delete to prevent hangs
- **Fixed Shell Terminal hardcoded IP** - Shell was connecting to hardcoded IP instead of configured PS5 address
- **Fixed ETA jumping** - ETA no longer jumps erratically during uploads
- **Fixed speed dropping to 0** - Sliding window prevents speed display from showing 0 during pauses
- **Fixed files remaining counter** - Prevented negative values in remaining files display

---

## 📝 What's New in v3.2.0

### 🎯 Improvements:

#### 1. 💾 Accurate Storage Display
- **Storage now matches PS5 UI** - Shows ~848 GB instead of raw 872 GB
- **Accounts for reserved space** - Same calculation as PS5 system

#### 2. 📱 Mobile App Improvements
- **Multi-select mode** - Toggle button to select multiple files/folders
- **Folder browser** - Visual folder picker for Copy/Move destinations
- **Sorted file list** - Folders always displayed at top
- **Exit confirmation** - Prompt when pressing back button
- **Delete folders** - Now supports folder deletion (not just files)
- **Batch operations** - Delete/download multiple items at once

#### 3. 🐛 Bug Fixes
- **Fixed empty folder deletion** - No more "Unexpected response" error
- **Fixed Copy/Move crash** - No longer crashes when selecting destination
- **Fixed multi-select count** - Correct item count in delete confirmation
- **Fixed folder navigation** - Can enter folders after actions complete

---

## 📝 What's New in v3.1.0

### 🚀 Performance Improvements:

#### 1. ⚡ Parallel Chunked Uploads (104 MB/s!)
- **Large files (>100MB)** are now split into 500MB chunks
- **4 parallel connections** upload chunks simultaneously
- **Result:** ~104 MB/s upload speed (2x faster than before!)
- **Fixed race condition:** First chunk creates file before others start
- **Fixed closure bug:** Correct offset/size capture for parallel tasks

#### 2. 📊 Real-time Progress Display
- **Speed display** now updates in real-time during chunked uploads
- **ETA display** shows accurate time remaining
- **Progress bar** correctly shows 0-100% for chunked files
- No more speed dropping to 0 during large file uploads

#### 3. 💾 Storage Display Improvements
- **Changed label** to "Data Storage (/data)" for clarity
- **Uses statfs with f_bavail** for more accurate available space
- **Real-time updates** every 5 seconds when connected

---

## 📝 What's New in v3.0.0

### 🐛 Critical Bug Fixes:

#### 1. 🔧 Path Normalization Fix
- **Fixed:** Double-slash paths (`//mnt/ext1/...`) that caused directory creation to fail
- **Affected:** All file operations (upload, download, delete, rename, copy, move)
- **Solution:** Added `NormalizePath()` function in both client and server
- **Result:** 100% reliable directory creation and file uploads

#### 2. 📱 Android Mobile Client
- **NEW:** Full-featured Android app for PS5 file management
- **Multi-PS5 Profiles** - Save and switch between multiple PS5 consoles
- Upload files from phone to PS5
- Download files from PS5 with Share option
- Browse PS5 filesystem
- Favorites for quick navigation
- Debug Log with Copy to clipboard
- Transfer History tracking

#### 3. 🛠️ Server Improvements
- Path normalization in all handler functions
- More robust error handling
- Improved stability for parallel uploads

---

## 📝 What's New in v2.1.0

### 🚀 Performance Optimizations:

#### 1. ⚡ Massive Upload Speed Boost (88-110 MB/s)
- **Server-side:** Replaced `fwrite()` with direct `write()` syscalls
- **Client-side:** 8 parallel large file uploads (optimal for PS5 disk)
- **Result:** 80-110 MB/s aggregate upload speed on Gigabit Ethernet
- **Peak bursts:** Up to 2.05 GB/s when hitting disk cache
- **Per-file:** 11-14 MB/s sustained per connection

#### 2. 📊 Transfer History
- Complete history of all uploads and downloads
- Success/Failed status tracking with error messages
- Speed statistics (average, min, max)
- Export to CSV/JSON for analysis
- Persistent storage across sessions

#### 3. 🔄 Auto-Clear History on Startup
- Optional checkbox to clear history automatically
- Useful for keeping UI clean between sessions
- Setting saved in `ps5_upload_settings.json`

#### 4. 🖥️ Maximized Window UI
- Application opens in full-screen mode by default
- Better visibility for large file transfers
- Can be resized/restored as needed

### Technical Improvements:
- ✅ **16MB socket buffers** (up from 4MB) for maximum throughput
- ✅ **Per-file mutex locking** - Parallel writes without race conditions
- ✅ **File pre-allocation** - Reduces disk fragmentation for large files
- ✅ **Direct syscalls** - Bypasses stdio buffering overhead

---

## 📝 What's New in v2.0.0

### 🎉 4 Major New Features:

#### 1. 📥 Download Files (PS5 → PC)
- Right-click any file → "⬇️ Download to PC"
- Save file dialog for destination selection
- Real-time progress tracking with speed display
- Optimized with sendfile for maximum speed

#### 2. 🔍 File Search
- Search box in PS5 Files panel
- Real-time filtering as you type
- Case-insensitive search
- Quick "Clear" button to reset

#### 3. ⭐ Favorites/Bookmarks
- Save frequently used PS5 paths
- Quick dropdown navigation
- Add/Remove favorite paths
- Persistent storage in JSON

#### 4. 🎮 Multi-PS5 Support
- Save multiple PS5 profiles (IP + name)
- Quick switch between different PS5 consoles
- Dropdown profile selector
- Persistent profile storage

### Previous Stability (v1.3.0):
- ✅ **Zero connection drops** - 5 minute socket timeout + aggressive keepalive
- ✅ **Fully responsive UI** - Async updates + throttled logging
- ✅ **No memory leaks** - Proper resource disposal
- ✅ **Tested:** 42,801 files uploaded with 100% success rate

---

## 📝 License

This software is provided as-is for personal use.

---

## 👤 Author

**Manos**

Created with ❤️ for the PS5 homebrew community

---

## 🙏 Credits

- PS5 SDK
- etaHEN
- Inspired by ps5upload by PhantomPtr
- PS5 homebrew community

---

## 📞 Support

For issues or questions, please open an issue on GitHub.

---

**Enjoy blazing-fast file transfers on your PS5!** 🚀
