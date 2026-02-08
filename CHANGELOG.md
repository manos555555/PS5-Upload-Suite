# Changelog - PS5 Upload Suite

## Version 4.2.1 - Bugfix Release (February 8, 2026)

### 🐛 Bug Fixes

#### UI Scrolling Fix
- **Fixed connection panel eating screen space** - Added `MaxHeight` constraint (250px) and `ScrollViewer` to the connection/payload settings panel
- **Fixed header overflow** - Added `MaxHeight="120"` to header row
- **Fixed content area too small** - Added `MinHeight="200"` to main content area
- When the Auto-send Payload Expander was open, it consumed half the screen leaving no room for file lists

#### File Counting Fix
- **Fixed chunked files never counting as completed** - `fileChunkCounts` was set to the actual number of chunks (e.g., 4 for a 2GB file), but `UploadFileParallelAsync` handles all chunks internally as a single Task. The main loop only saw 1 task completion per file, so chunked files never reached their completion threshold
- Now correctly sets `fileChunkCounts = 1` per file since each file = 1 task

#### Progress Display Fix
- **Fixed progress appearing frozen during heavy uploads** - All UI updates used `DispatcherPriority.Background` (lowest priority), which got starved when 24 parallel connections flooded the dispatcher queue
- Upgraded `UpdateUploadStats` to `DispatcherPriority.Normal`
- Upgraded per-file progress bars to `DispatcherPriority.Render`
- Progress now updates smoothly even at 95%+ network saturation

---

## Version 4.2.0 - Game Mounter Integration (February 7, 2026)

### 🎮 Game Mounter Integration
- **Mount Games Button** - New green "🎮 Mount Games" button in the client UI
- One-click mounting of all uploaded games directly from the PS5 Upload Suite
- Button disabled when disconnected, shows "⏳ Mounting..." during operation

### 🔍 Multi-Path Game Scanning
- Scans **6 locations** for games:
  - `/data/etaHEN/games` (internal storage)
  - `/mnt/usb0/games` through `/mnt/usb3/games` (USB drives)
  - `/mnt/ext0/games` (M.2 SSD)
- First pass counts total games, second pass processes them
- PS5 notifications show real-time progress: "Mounting 3/10 (30%) - Game Name"

### 🔄 Smart Mount Features
- **Duplicate Detection** - Same title ID across multiple drives mounted only once
- **Auto-Cleanup** - `auto_unmount_deleted_games()` removes stale mount entries
- **Already Mounted Detection** - Skips games that are already mounted via nullfs
- **sce_sys Validation** - Verifies game directory has valid `sce_sys/` before mounting

### 🔧 Payload Changes
- **New Command** - `CMD_MOUNT_GAMES` (0x30) added to protocol
- **nullfs Mount** - Games mounted to `/system_ex/app/<title_id>`
- **DRM Patching** - Automatic `applicationDrmType` fix in `param.json`
- **Metadata Copy** - `sce_sys` copied to `/user/app/` and `/user/appmeta/`
- **mount.lnk** - Tracks mount source path for cleanup and remount
- **Safe Delete** - Replaced `system("rm -rf ...")` with `rmdir_recursive()` to prevent hangs
- **remount_system_ex()** - Remounts `/system_ex` as writable for game installation

### 🖥️ Client Changes
- **Protocol.cs** - Added `MountGames = 0x30` enum and `MountGamesAsync()` method
- **MainWindow.xaml** - Green "🎮 Mount Games" button between Upload and Cancel
- **MainWindow.xaml.cs** - Click handler with progress display and auto-registration
- **Auto Registration** - If new games detected, automatically runs `game_mounter.elf` via shell for home screen registration

### ⚡ Client Improvements
- **Real-Time Speed Display** - New sliding window algorithm (10 samples over 5 seconds) for accurate real-time upload speed
- **Smoother ETA** - Blended speed calculation (60% real-time + 40% average) with better time formatting (hours/minutes/seconds)
- **Speed Display** - Shows both real-time and average speed: `Speed: 95 MB/s (avg 88 MB/s)`
- **Duplicate File Dialog** - New `DuplicateFileDialog` for handling file conflicts during upload
- **Move Dialog Refactor** - Replaced inline move dialog code with reusable `ShowPathPickerDialogAsync()`
- **FormatFileSize Refactor** - Moved to static `FileUtils.FormatFileSize()` class for code reuse
- **ETA Smoothing** - Reduced `ETASmoothingFactor` from 0.3 to 0.15 for less jumpy ETA display

### 🐛 Bug Fixes

#### Payload Fixes
- **Fixed IOVEC_ENTRY(NULL) crash** - `strlen(NULL)` with `-O3` optimization caused segfault in `remount_system_ex()`
- **Fixed libSceAppInstUtil startup crash** - Library cannot be loaded in etaHEN payload context (crashes dynamic linker). Switched to standalone `game_mounter.elf` for registration
- **Fixed dlopen crash** - `dlopen("libSceAppInstUtil.sprx")` crashes payload thread. Removed dynamic loading approach
- **Removed system() calls** - `system("rm -rf ...")` replaced with safe recursive `rmdir_recursive()` to prevent payload hangs
- **Fixed compile warnings** - Removed unused `#include <dlfcn.h>`, re-enabled `auto_unmount_deleted_games()`

#### Client Fixes
- **Fixed Shell Terminal hardcoded IP** - Shell was connecting to hardcoded `192.168.0.160` instead of the configured PS5 IP address
- **Fixed ETA jumping** - ETA no longer jumps erratically during uploads due to improved smoothing
- **Fixed speed dropping to 0** - Sliding window prevents speed display from showing 0 during brief pauses
- **Fixed files remaining counter** - Simplified calculation with `Math.Max(0, ...)` to prevent negative values

### 📝 Technical Notes
- `libSceAppInstUtil.sprx` is **not available** in etaHEN payload context (both static linking and dlopen crash)
- Game registration (`sceAppInstUtilAppInstallTitleDir`) is handled by the standalone `game_mounter.elf` which runs as a separate ELF with its own library dependencies
- The upload suite payload handles nullfs mounting, metadata copy, and DRM patching — everything except the final system registration

---

## Version 4.1.0 - Memory Optimization & Large File Handling (February 5, 2026)

### 🧠 Memory & Performance Improvements

#### 1. Large File Count Support (156K+ Files)
- **Fixed crash** with games containing 156,253+ files (e.g., Astrobot)
- **Removed massive dictionary** - No longer creates 156K FileInfo objects at once
- **Incremental calculation** - Total bytes calculated progressively during scan
- **Memory efficient** - Handles unlimited file counts without OutOfMemoryException

#### 2. Lock-Free Progress Tracking
- **ConcurrentDictionary** - Replaced Dictionary with thread-safe collections
- **Zero lock contention** - Eliminated `lock (_progressLock)` blocks
- **Atomic operations** - Uses `Interlocked.Add()` for progress updates
- **Smooth UI** - No stuttering from lock contention

#### 3. Auto Memory Cleanup
- **Completed files removed** - Dictionaries cleaned after file completion
- **Reduced memory footprint** - Only active uploads tracked
- **Prevents memory overflow** - Long-running uploads stay stable

#### 4. Reduced UI Stuttering
- **Batch logging** - Small files logged every 50 completions (up from 25)
- **Less UI pressure** - Fewer `Dispatcher.Invoke()` calls
- **Smoother experience** - Especially noticeable with 100K+ files

### 🔧 Technical Changes
- Changed `_fileProgressBytes` from Dictionary to ConcurrentDictionary
- Changed `_fileChunkProgressBytes` from Dictionary to ConcurrentDictionary  
- Changed `_chunkLogLastBytes` from Dictionary to ConcurrentDictionary
- Removed `fileSizeLookup` dictionary creation (caused OutOfMemoryException)
- Increased `SmallFileLogBatchSize` from 25 to 50
- Added dictionary cleanup in file completion handlers

### 🐛 Bug Fixes
- Fixed application crash when uploading games with 156K+ files
- Fixed UI stuttering during small file uploads
- Fixed memory overflow from massive dictionary allocations
- Fixed lock contention causing UI freezes

### 📊 Tested With
- **Astrobot**: 156,253 files, 1,724 directories - **53 minutes total**
- **Result**: Zero crashes, smooth upload, stable memory usage

---

## Version 4.0.0 - Retry Feature & Stability Improvements (January 27, 2026)

### ✨ New Features

#### 🔄 Retry Failed Transfers
- **Right-click context menu** on failed transfers
- **Retry Upload** - Re-queue failed files for upload
- **Remove from List** - Clear individual failed items
- **Persistent paths** - Failed transfers store local and remote paths
- **Smart retry** - Validates file existence before retry

#### 🗑️ Clear All Button Fix
- **Transfer History Clear All** now works correctly
- **Confirmation dialog** before clearing history
- **Clears both** completed and failed transfers

### 🔧 Performance & Stability

#### Connection Stability
- **Reverted 16MB buffer** - Caused connection drops with 16 parallel connections
- **Stable 8MB buffer** - Proven reliable for high-speed transfers
- **TCP NoDelay** - Already enabled for optimal performance
- **16MB socket buffers** - Maintained for maximum throughput

#### Upload Performance
- **Large files (>100MB)**: 30-36 MB/s per file
- **Small files (<100MB)**: 8-12 MB/s per file
- **4 parallel large files** - Prevents PS5 memory exhaustion
- **16 total connections** - Optimal for mixed workloads

### 🐛 Bug Fixes
- Fixed Clear All button not clearing transfer history
- Fixed connection drops when using oversized buffers
- Added proper event handlers for failed transfer context menu
- Improved error handling for retry operations

### 📝 Code Quality
- Added comprehensive inline documentation
- Improved code organization and readability
- Better error messages for failed operations
- Enhanced UI feedback for user actions

---

## Version 3.3.0 - Smart Search & Enhanced Mobile (January 26, 2026)

### 🔍 Smart Search Feature
- **Full Filesystem Indexing** - Index entire PS5 filesystem (200,000+ files)
- **Instant Search** - Lightning-fast search with wildcards (`*.pkg`, `*loader*`)
- **Size Filters** - Search by file size (`size:>1GB`, `size:<100MB`)
- **Case-Insensitive** - Matches both filename and full path
- **Navigate from Search** - Double-click results to jump to folder (Desktop) / Tap to navigate (Mobile)
- **Index Status** - Real-time indexing progress with file/directory counts
- **Smart Indexing** - Skips problematic directories (`/dev`, `/proc`, `/sys`)

### 📱 Mobile App v1.2
- **🔍 Search Tab** - Full Smart Search functionality on Android
- **Navigate to Location** - Tap search results to navigate to folder
- **Copy Path** - Copy file paths to clipboard from search results
- **Connection Manager** - Singleton pattern for reliable IP/port management
- **Improved Navigation** - Fixed MainPage reference for seamless navigation

### 🖥️ Desktop Client Updates
- **Search Tab** - New dedicated tab for Smart Search
- **💻 Shell Terminal** - Execute commands directly on PS5
  - Run system commands remotely
  - Real-time output display
  - Command history
  - Working directory support
- **Equal Panel Layout** - Local Files, PS5 Files, and Right panel now equal width
- **Improved UI** - Index Status and buttons positioned above search box
- **Debounced Search** - Smooth typing experience with 300ms debounce

### 🔧 Payload Updates
- **Indexing Commands** - `CMD_INDEX_START` (0x40), `CMD_INDEX_STATUS` (0x41), `CMD_SEARCH_INDEX` (0x42)
- **In-Memory Index** - Linked list structure for fast search
- **Wildcard Matching** - Support for `*` and `?` wildcards
- **Thread-Safe** - Mutex-protected index operations
- **Root Path Support** - Can index from `/` (entire filesystem)

### 🐛 Bug Fixes
- Fixed indexing hangs when scanning root directory
- Fixed search protocol compatibility between mobile and desktop
- Fixed navigation stack issues in mobile app
- Fixed nullable reference warnings in mobile code
- Added proper error handling for index operations

---

## Version 3.2.1 - Privacy & Bug Fixes (January 24, 2026)

### 🔒 Privacy Improvements
- **Removed disk space display** - No longer shows PS5 storage capacity (Free/Used/Total)
- **Fixed storage query bug** - Resolved syntax errors in statvfs code that could cause crashes
- **Privacy-focused** - Application no longer queries or displays sensitive storage information

### 🐛 Bug Fixes
- Fixed potential crash from malformed storage query code
- Removed unused protocol busy flag that caused compiler warnings
- Cleaned up all storage-related UI elements and backend code

---

## Version 3.2.0 - Mobile Improvements & Bug Fixes (January 24, 2026)

### 🎯 Improvements

#### 💾 Accurate Storage Display
- **Storage now matches PS5 UI** - Shows ~848 GB instead of raw 872 GB
- **Accounts for reserved space** - Same calculation as PS5 system
- Uses proper calculation: `(total_blocks - reserved) * block_size`

#### 📱 Mobile App Improvements
- **Multi-select mode** - Toggle button (☑️/✅) to select multiple files/folders
- **Folder browser** - Visual folder picker for Copy/Move destinations with recursive navigation
- **Sorted file list** - Folders always displayed at top, then files (alphabetically)
- **Exit confirmation** - Prompt dialog when pressing back button
- **Delete folders** - Now supports folder deletion (not just files)
- **Batch operations** - Delete/download multiple items at once

#### 3. 🐛 Bug Fixes
- **Fixed empty folder deletion** - No more "Unexpected response" error when deleting empty directories
- **Fixed Copy/Move crash** - No longer crashes when selecting destination folder (saved file info before browsing)
- **Fixed multi-select count** - Correct item count in delete confirmation dialog
- **Fixed folder navigation** - Can enter folders after actions complete (auto-exit multi-select mode)

---

## Version 3.1.0 - Parallel Chunked Uploads (January 2026)

### 🚀 Performance Improvements

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

## Version 3.0.0 - Mobile Client & Path Fixes (January 2026)

### 🐛 Critical Bug Fixes

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
- Create new folders
- Rename files/folders
- Copy files/folders
- Move files/folders
- Delete files
- Favorites for quick navigation
- Debug Log with Copy to clipboard
- Transfer History tracking

#### 3. 🛠️ Server Improvements
- Path normalization in all handler functions
- More robust error handling
- Improved stability for parallel uploads

---

## Version 2.1.0 - Performance & History (January 2026)

### 🚀 Performance Optimizations

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

### Technical Improvements
- ✅ **16MB socket buffers** (up from 4MB) for maximum throughput
- ✅ **Per-file mutex locking** - Parallel writes without race conditions
- ✅ **File pre-allocation** - Reduces disk fragmentation for large files
- ✅ **Direct syscalls** - Bypasses stdio buffering overhead

---

## Version 2.0.0 - Download & Multi-PS5 (January 2026)

### 🎉 4 Major New Features

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

---

## Version 1.3.0 - Stable Release (January 13, 2026)

### 🎯 Major Achievement
**Complete stability overhaul** - Fixed all critical bugs causing connection drops, UI freezes, and upload failures. System now handles **42,801+ files** with **zero errors** and **full gigabit speeds**.

---

## 🔧 Bug Fixes

### **PAYLOAD (Server-Side) - 7 Critical Fixes**

#### 1. **Per-File Mutex Implementation**
- **Issue:** Multiple threads could write to the same file simultaneously causing data corruption
- **Fix:** Implemented per-file mutex system using hash map for file path locking
- **Impact:** Prevents race conditions during chunked uploads

#### 2. **Memory/Mutex Cleanup on Disconnect**
- **Issue:** Mutexes and memory not properly released when client disconnected
- **Fix:** Added proper cleanup in client disconnect handler
- **Impact:** Prevents memory leaks and mutex deadlocks

#### 3. **fflush() Race Condition**
- **Issue:** `fflush()` called outside file mutex, causing potential data corruption
- **Fix:** Moved `fflush()` inside mutex lock in `handle_upload_chunk`
- **Location:** `main.c:869-890`
- **Impact:** Ensures data integrity during concurrent writes

#### 4. **File Creation Race Condition**
- **Issue:** Multiple threads could attempt to create/open same file simultaneously
- **Fix:** Added `pthread_mutex_lock` around file creation and `fseeko` in `handle_start_upload`
- **Location:** `main.c:814-844`
- **Impact:** Prevents file corruption during parallel uploads

#### 5. **Global Scan Counter Issues**
- **Issue:** Static variables in recursive `count_files_recursive` caused stale values
- **Fix:** Moved `scan_count` and `last_scan_notify` to global scope
- **Location:** `main.c:324-366`
- **Impact:** Accurate progress reporting during folder deletion

#### 6. **Directory Counting Bug**
- **Issue:** `count_files_recursive` only counted files, not directories
- **Fix:** Added directory counting in recursive function
- **Location:** `main.c:350-351`
- **Impact:** Correct file count for folders with only subdirectories

#### 7. **Socket Timeout for Large Files** ⭐ **CRITICAL FIX**
- **Issue:** PS5 closed connections during large file uploads (100MB+) due to default timeout
- **Fix:** Added 5-minute socket timeout (`SO_RCVTIMEO`/`SO_SNDTIMEO`)
- **Location:** `main.c:1142-1148`
- **Impact:** **Eliminates all connection drops** - enables stable upload of 15GB+ files
- **Additional:** Aggressive TCP keepalive (10s idle, 5s interval, 3 probes)

---

### **CLIENT (Windows App) - 8 Critical Fixes**

#### 1. **ConnectAsync Timeout Memory Leak**
- **Issue:** `TcpClient` not disposed on connection timeout
- **Fix:** Added proper `Dispose()` call in timeout exception handler
- **Location:** `Protocol.cs:62-67`
- **Impact:** Prevents memory leaks during connection failures

#### 2. **ConnectAsync Exception Memory Leak**
- **Issue:** `TcpClient` not disposed on general connection exceptions
- **Fix:** Added proper `Dispose()` call in exception handler
- **Location:** `Protocol.cs:81-84`
- **Impact:** Prevents memory leaks during network errors

#### 3. **Disconnect Memory Leak**
- **Issue:** `NetworkStream` and `TcpClient` not properly disposed
- **Fix:** Added explicit `Dispose()` calls for both objects
- **Location:** `Protocol.cs:91-92`
- **Impact:** Prevents memory leaks during normal disconnection

#### 4. **Upload Deadlock During Chunked Uploads**
- **Issue:** Tasks never removed from `activeTasks` when connection index mismatched
- **Fix:** Always remove completed task regardless of connection index validity
- **Location:** `MainWindow.xaml.cs:692-734`
- **Impact:** Prevents infinite wait during parallel chunked uploads

#### 5. **Chunking Disabled for Stability** ⭐ **CRITICAL FIX**
- **Issue:** PS5 cannot handle concurrent writes to same file, even with per-file mutex
- **Fix:** Disabled chunking completely - use single connection per file
- **Location:** `MainWindow.xaml.cs:611-614`
- **Impact:** **Eliminates all "connection aborted" errors**
- **Rationale:** Even with 2 chunks, PS5 actively closes connections during concurrent writes

#### 6. **UI Freeze During Uploads**
- **Issue:** `Dispatcher.Invoke()` blocked UI thread when uploading thousands of files
- **Fix:** Replaced with `Dispatcher.InvokeAsync()` in `UpdateUploadStats`
- **Location:** `MainWindow.xaml.cs:115-158`
- **Impact:** **Fully responsive UI** during uploads

#### 7. **Log Flooding Causing UI Freeze**
- **Issue:** LogTextBox filled with hundreds of thousands of entries, causing UI slowdown
- **Fix:** 
  - Throttle verbose messages (log every 50th message)
  - Limit log size to 1000 lines (remove oldest 200 when limit reached)
  - Use `InvokeAsync` with `Background` priority
- **Location:** `MainWindow.xaml.cs:165-206`
- **Impact:** **Prevents UI freeze** and memory issues

#### 8. **Optimal Parallel Connection Strategy**
- **Issue:** Need to balance speed vs stability
- **Fix:** Use **6 parallel single-connection uploads** (no chunking)
- **Impact:** 
  - **Full gigabit speeds** (60-150 MB/s total throughput)
  - **Zero connection drops**
  - **Maximum stability**

---

## 📊 Performance Improvements

### Before Fixes:
- ❌ Connection drops every 100-200 files
- ❌ UI completely frozen during uploads
- ❌ Chunked uploads failed with "connection aborted" errors
- ❌ Memory leaks causing application slowdown
- ❌ Upload deadlocks requiring application restart

### After Fixes:
- ✅ **42,801+ files uploaded with ZERO errors**
- ✅ **Fully responsive UI** throughout entire upload
- ✅ **Consistent speeds:** 11-190 MB/s per connection
- ✅ **Total throughput:** 60-150 MB/s (full gigabit)
- ✅ **No memory leaks** - stable memory usage
- ✅ **No deadlocks** - perfect task management

---

## 🎯 Technical Architecture

### Payload (PS5 Server):
```c
// 5 minute socket timeout for large files
struct timeval tv;
tv.tv_sec = 300;
setsockopt(client_sock, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
setsockopt(client_sock, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));

// Aggressive TCP keepalive
int keepidle = 10;   // Start after 10s idle
int keepintvl = 5;   // Send probe every 5s
int keepcnt = 3;     // Drop after 3 failures
setsockopt(client_sock, IPPROTO_TCP, TCP_KEEPIDLE, &keepidle, sizeof(keepidle));
setsockopt(client_sock, IPPROTO_TCP, TCP_KEEPINTVL, &keepintvl, sizeof(keepintvl));
setsockopt(client_sock, IPPROTO_TCP, TCP_KEEPCNT, &keepcnt, sizeof(keepcnt));

// Per-file mutex for concurrent uploads
pthread_mutex_t* file_mutex = get_file_mutex(file_path);
pthread_mutex_lock(file_mutex);
fwrite(buffer, 1, bytes_received, fp);
fflush(fp);
pthread_mutex_unlock(file_mutex);
```

### Client (Windows):
```csharp
// 6 parallel single-connection uploads
const int MaxParallelUploads = 6;

// Chunking disabled for stability
if (false) // Chunking permanently disabled
{
    // PS5 cannot handle concurrent writes to same file
}

// Throttled logging for responsive UI
if (message.Contains("📊") || message.Contains("⬆️ Uploading:"))
{
    _logCounter++;
    if (_logCounter % 50 != 0)
        return; // Skip verbose messages
}

// Async UI updates
Dispatcher.InvokeAsync(() => {
    // Update UI without blocking
}, DispatcherPriority.Background);
```

---

## 🚀 Tested Configuration

### Test Case:
- **Files:** 42,801 files
- **Total Size:** ~7.52 GB
- **File Types:** .xpps, .pak, .mp4, .bk2, .bank, .json, .prx, .sprx
- **Largest File:** 15.96 GB (ac2-ps5.pak)

### Results:
- **Success Rate:** 100% (zero errors)
- **Average Speed:** 60-150 MB/s
- **Peak Speed:** 190 MB/s per connection
- **UI Responsiveness:** Fully responsive throughout
- **Memory Usage:** Stable (no leaks)
- **Completion Time:** ~1.7 hours for 42,801 files

---

## 📝 Known Limitations

1. **Chunking Disabled:** While this reduces speed for individual large files, it's necessary for PS5 stability
2. **6 Connection Limit:** More connections cause overhead without speed benefit
3. **PS5 Hardware Constraint:** PS5 cannot handle concurrent writes to same file, even with proper locking

---

## 🔄 Migration Guide

### From v1.2 to v1.3:
1. **Upload new payload** (`ps5_upload_server.elf`) to PS5
2. **Restart payload** with elfldr
3. **Use new client** (`PS5Upload.exe`)
4. **No configuration changes needed** - works out of the box

### Breaking Changes:
- None - fully backward compatible

---

## 👨‍💻 Developer Notes

### Why Chunking Was Disabled:
Multiple attempts were made to enable chunking with various configurations:
- 4 chunks: Connection drops
- 3 chunks: Connection drops  
- 2 chunks: Connection drops

**Root Cause:** PS5 FreeBSD kernel actively closes TCP connections when multiple threads perform concurrent `fwrite()` operations on the same file, even with proper per-file mutex locking. The 5-minute socket timeout helps, but doesn't solve the fundamental issue of concurrent writes.

**Solution:** Single connection per file ensures only one thread writes to each file, eliminating the issue entirely.

### Why 6 Parallel Connections:
- **Less than 6:** Underutilizes network bandwidth
- **More than 6:** Increases CPU/memory overhead without speed benefit
- **6 connections:** Perfect balance for PS5 hardware

---

## 🙏 Credits

**Developed by:** Manos  
**Testing:** Extensive real-world testing with 42,801+ files  
**Platform:** PS5 (FreeBSD) + Windows 10/11  

---

## 📄 License

This software is provided as-is for educational and personal use.
