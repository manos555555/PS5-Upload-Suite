# 🎉 PS5 Upload Suite v2.0.0 - Feature-Rich Release

## 🚀 What's New

This is a **major feature release** adding 4 powerful new capabilities to the PS5 Upload Suite!

---

## ✨ New Features

### 1. 📥 Download Files (PS5 → PC)

Finally! You can now download files from your PS5 back to your PC.

**How to use:**
- Right-click any file in PS5 Files panel
- Select "⬇️ Download to PC"
- Choose where to save the file
- Watch the progress with real-time speed tracking

**Technical Details:**
- Uses sendfile optimization for maximum speed
- Zero-copy transfer on PS5 side
- Same high-speed protocol as uploads
- Full progress tracking

---

### 2. 🔍 File Search

Quickly find files in large directories without scrolling.

**How to use:**
- Type in the Search box below Current Path
- Files are filtered in real-time as you type
- Case-insensitive search
- Click "✖ Clear" to reset

**Perfect for:**
- Finding specific game saves
- Locating config files
- Searching through large mod folders

---

### 3. ⭐ Favorites/Bookmarks

Save your frequently used PS5 paths for instant navigation.

**How to use:**
- Navigate to any path (e.g., `/data/games`)
- Click "⭐ Add" to save it
- Use the Favorites dropdown to quickly jump to saved paths
- Click "🗑️ Remove" to delete a favorite

**Saved paths persist between sessions!**

---

### 4. 🎮 Multi-PS5 Support

Manage multiple PS5 consoles with ease.

**How to use:**
- Enter a PS5 IP address
- Click "💾 Save Profile" and give it a name (e.g., "Living Room PS5")
- Use the Profile dropdown to quickly switch between PS5s
- Click "🗑️ Delete Profile" to remove saved profiles

**Perfect for:**
- Users with multiple PS5 consoles
- Testing on different systems
- Switching between home and friend's PS5

---

## 🔧 Technical Improvements

### Server-Side (payload)
- Added `CMD_DOWNLOAD_FILE` (0x13) command
- Implemented sendfile-based download handler
- Fixed `LIST_DIR` to return actual file sizes (not 0)
- Zero-copy file transfer for downloads

### Client-Side
- Added `DownloadFileAsync()` method to Protocol
- Implemented search filtering with `ObservableCollection`
- Added JSON-based profile and favorites persistence
- Custom ComboBox templates for dark theme consistency
- Improved UI responsiveness

---

## 📊 Statistics

**Lines of Code Added:** ~500+ lines  
**New Protocol Commands:** 1 (DOWNLOAD_FILE)  
**New UI Elements:** 3 panels (Search, Favorites, Profiles)  
**Persistent Storage Files:** 2 (ps5_profiles.json, ps5_favorites.json)

---

## 🎨 UI Improvements

- All ComboBox dropdowns now have proper dark theme
- All buttons show full text without cutoff
- Consistent styling across all new features
- Responsive layout that adapts to content

---

## 📦 Files Included

### Server
- `payload/ps5_upload_server.elf` - Updated with download support

### Client
- `client/bin/Release/net6.0-windows/win-x64/PS5Upload.exe` - Full v2.0 client

---

## ⬆️ Upgrading from v1.3.0

1. **Replace server payload:**
   - Copy new `ps5_upload_server.elf` to `/data/etaHEN/payloads/`
   - Reload with elfldr

2. **Replace client:**
   - Close old PS5Upload.exe
   - Replace with new version
   - Your settings will be preserved

**Note:** v2.0 creates two new files:
- `ps5_profiles.json` - Saved PS5 profiles
- `ps5_favorites.json` - Saved favorite paths

---

## 🐛 Known Issues

None reported yet! This is a stable release built on top of the rock-solid v1.3.0 foundation.

---

## 🔮 Future Features (Not in v2.0)

Still on the roadmap:
- Disk Space Warning before upload
- Duplicate File Detection
- File Verification (SHA256)
- Resume Interrupted Uploads
- Queue Management (reorder, pause)
- Transfer History
- Scheduled Uploads

---

## 🙏 Thanks

Special thanks to the PS5 homebrew community for testing and feedback!

---

**Enjoy the new features!** 🚀

**- Manos**
