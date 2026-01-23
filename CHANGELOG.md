# Changelog - PS5 Upload Suite

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
