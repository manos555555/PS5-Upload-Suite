# GitHub Upload Instructions - PS5 Upload Suite

## 📁 Correct Folder to Upload

**Source Path:**
```
C:\Users\HACKMAN\Desktop\ps5 test\ps5_rom_keys\ps5_upload_suite\client\bin\Release\net6.0-windows\win-x64\
```

This folder contains the **complete, self-contained Windows executable** with all dependencies.

---

## 🗑️ Step 1: Delete Old Release on GitHub

1. Go to your GitHub repository
2. Navigate to **Releases** tab
3. Find the old/incorrect release
4. Click **Delete** on the old release
5. Confirm deletion

**Or delete via Git:**
```bash
# Delete old tag
git tag -d v1.0
git push origin :refs/tags/v1.0

# Delete old files from repository
git rm -r ps5upload-windows-x64/
git commit -m "Remove incorrect Windows build"
git push origin main
```

---

## 📦 Step 2: Create Release Package

### Option A: Via PowerShell (Automated)

```powershell
# Navigate to project root
cd "C:\Users\HACKMAN\Desktop\ps5 test\ps5_rom_keys\ps5_upload_suite"

# Create release directory
New-Item -ItemType Directory -Force -Path ".\github_release"

# Copy correct files
Copy-Item -Path ".\client\bin\Release\net6.0-windows\win-x64\*" -Destination ".\github_release\" -Recurse

# Copy payload
Copy-Item -Path ".\payload\ps5_upload_server.elf" -Destination ".\github_release\"

# Create ZIP
Compress-Archive -Path ".\github_release\*" -DestinationPath ".\PS5Upload-Windows-x64-v1.0.zip" -Force

Write-Host "Release package created: PS5Upload-Windows-x64-v1.0.zip"
```

### Option B: Manual

1. Create new folder: `PS5Upload-Windows-x64`
2. Copy **ALL files** from `win-x64\` folder
3. Add `ps5_upload_server.elf` from `payload\` folder
4. Create ZIP: `PS5Upload-Windows-x64-v1.0.zip`

---

## ⬆️ Step 3: Upload to GitHub

### Via GitHub Web Interface:

1. Go to your repository on GitHub
2. Click **Releases** → **Create a new release**
3. Fill in:
   - **Tag:** `v1.0`
   - **Title:** `PS5 Upload Suite v1.0 - Windows x64`
   - **Description:**
     ```markdown
     # PS5 Upload Suite v1.0
     
     ## Windows x64 Release
     
     Self-contained executable for Windows 10/11 (64-bit)
     No .NET Runtime installation required!
     
     ## What's Included:
     - PS5Upload.exe (Windows GUI client)
     - ps5_upload_server.elf (PS5 server payload)
     - All required dependencies
     
     ## How to Use:
     1. Extract ZIP file
     2. Run PS5Upload.exe
     3. Load ps5_upload_server.elf to PS5 (via etaHEN)
     4. Connect and upload files!
     
     ## Requirements:
     - Windows 10/11 (64-bit)
     - PS5 with etaHEN 2.0+ or GoldHEN
     - Network connection to PS5
     
     ## File Size:
     - Full package: ~50 MB
     - Includes all .NET 6.0 dependencies
     ```
4. **Attach files:**
   - Upload `PS5Upload-Windows-x64-v1.0.zip`
5. Click **Publish release**

### Via Git Command Line:

```bash
# Navigate to repository
cd "C:\Users\HACKMAN\Desktop\ps5 test\ps5_rom_keys\ps5_upload_suite"

# Add correct files
git add client/bin/Release/net6.0-windows/win-x64/
git commit -m "Add correct Windows x64 build with all dependencies"
git push origin main

# Create and push tag
git tag -a v1.0 -m "PS5 Upload Suite v1.0 - Windows x64"
git push origin v1.0

# Upload release asset via GitHub CLI (if installed)
gh release create v1.0 PS5Upload-Windows-x64-v1.0.zip --title "PS5 Upload Suite v1.0" --notes "Self-contained Windows x64 release"
```

---

## 📝 Step 4: Update Repository README

Add download link to main README.md:

```markdown
## Download

### Latest Release: v1.0

**Windows (x64):**
- [PS5Upload-Windows-x64-v1.0.zip](https://github.com/YOUR_USERNAME/ps5_upload_suite/releases/download/v1.0/PS5Upload-Windows-x64-v1.0.zip)
- Size: ~50 MB
- Self-contained (no .NET installation required)

**PS5 Server:**
- Included in Windows package: `ps5_upload_server.elf`
- Load via etaHEN or GoldHEN

### System Requirements:
- **Windows:** 10/11 (64-bit)
- **PS5:** Firmware with etaHEN 2.0+ or GoldHEN
```

---

## ✅ Verification Checklist

Before uploading, verify:

- [ ] Folder contains `PS5Upload.exe` (8+ MB)
- [ ] Folder contains `PS5Upload.dll`
- [ ] Folder contains `PS5Upload.runtimeconfig.json`
- [ ] Folder contains all DLL dependencies (100+ files)
- [ ] `ps5_upload_server.elf` is included
- [ ] ZIP file is created correctly
- [ ] Test: Extract ZIP and run `PS5Upload.exe` - should work without errors

---

## 🐛 Common Issues

### "The application requires .NET Runtime"
**Problem:** Wrong folder uploaded (missing dependencies)  
**Solution:** Use the `win-x64` folder, not `release` folder

### "Missing DLL files"
**Problem:** Incomplete upload  
**Solution:** Make sure ALL files from `win-x64` folder are included

### "Application won't start"
**Problem:** Corrupted ZIP or missing files  
**Solution:** Re-create ZIP from scratch, include all files

---

## 📊 File Structure for GitHub

```
PS5Upload-Windows-x64-v1.0.zip
├── PS5Upload.exe                    (Main executable)
├── PS5Upload.dll                    (Application library)
├── PS5Upload.runtimeconfig.json     (Runtime configuration)
├── ps5_upload_server.elf            (PS5 server payload)
├── Accessibility.dll                (Dependency)
├── D3DCompiler_47_cor3.dll         (Dependency)
├── DirectWriteForwarder.dll        (Dependency)
├── Microsoft.*.dll                  (100+ Microsoft dependencies)
├── PresentationCore.dll            (WPF dependency)
├── PresentationFramework.dll       (WPF dependency)
├── ReachFramework.dll              (WPF dependency)
├── System.*.dll                     (System dependencies)
├── UIAutomationClient.dll          (UI dependency)
├── UIAutomationProvider.dll        (UI dependency)
├── UIAutomationTypes.dll           (UI dependency)
├── WindowsBase.dll                 (Windows dependency)
├── WindowsFormsIntegration.dll     (Windows dependency)
├── api-ms-win-*.dll                (Windows API dependencies)
├── clrcompression.dll              (Compression)
├── clretwrc.dll                    (ETW)
├── clrjit.dll                      (JIT compiler)
├── coreclr.dll                     (CoreCLR runtime)
├── createdump.exe                  (Debug tool)
├── hostfxr.dll                     (Host)
├── hostpolicy.dll                  (Host policy)
├── mscordaccore.dll                (DAC)
├── mscordaccore_amd64_amd64_*.dll  (DAC)
├── mscordbi.dll                    (Debugger)
├── mscorlib.dll                    (Core library)
├── msquic.dll                      (QUIC)
├── ucrtbase.dll                    (C runtime)
├── vcruntime140.dll                (VC runtime)
└── vcruntime140_cor3.dll           (VC runtime)
```

**Total:** ~150 files, ~50 MB

---

## 🎯 Quick Commands Summary

```powershell
# 1. Create release package
cd "C:\Users\HACKMAN\Desktop\ps5 test\ps5_rom_keys\ps5_upload_suite"
Compress-Archive -Path ".\client\bin\Release\net6.0-windows\win-x64\*" -DestinationPath ".\PS5Upload-Windows-x64-v1.0.zip" -Force

# 2. Test locally
Expand-Archive -Path ".\PS5Upload-Windows-x64-v1.0.zip" -DestinationPath ".\test_extract" -Force
.\test_extract\PS5Upload.exe

# 3. Upload to GitHub (manual via web interface)
# Go to: https://github.com/YOUR_USERNAME/ps5_upload_suite/releases/new
# Upload: PS5Upload-Windows-x64-v1.0.zip
```

---

## ✅ Final Steps

1. **Delete old release** on GitHub
2. **Create ZIP** from `win-x64` folder
3. **Test ZIP** locally (extract and run)
4. **Upload to GitHub** as new release
5. **Update README** with download link
6. **Announce** the correct release

---

**The correct folder is:**
```
C:\Users\HACKMAN\Desktop\ps5 test\ps5_rom_keys\ps5_upload_suite\client\bin\Release\net6.0-windows\win-x64\
```

**This contains everything needed for a working Windows release!**
