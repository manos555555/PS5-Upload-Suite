# PS5 Upload Suite v1.2 - Complete Bug Fix Release

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "PS5 Upload Suite v1.2 - Complete Fix" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$sourceFolder = ".\client\bin\Release\net6.0-windows\win-x64"
$payloadFile = ".\payload\ps5_upload_server.elf"
$outputZip = ".\PS5Upload-Windows-x64-v1.2-Complete.zip"
$tempFolder = ".\github_release_v1.2_temp"

Write-Host "[INFO] Bug Fixes in v1.2:" -ForegroundColor Yellow
Write-Host "  CLIENT:" -ForegroundColor Cyan
Write-Host "    1. Connection timeout: 5 seconds (fast fail on wrong IP)" -ForegroundColor Green
Write-Host "    2. Folder deletion timeout: 60 seconds client-side" -ForegroundColor Green
Write-Host "  SERVER:" -ForegroundColor Cyan
Write-Host "    3. Async folder deletion (no more timeout on large folders!)" -ForegroundColor Green
Write-Host ""

# Check files
if (-not (Test-Path $sourceFolder)) {
    Write-Host "[ERROR] Client folder not found!" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $payloadFile)) {
    Write-Host "[ERROR] Server payload not found!" -ForegroundColor Red
    exit 1
}

Write-Host "[1/5] Checking files..." -ForegroundColor Yellow
$fileCount = (Get-ChildItem -Path $sourceFolder -File).Count
$payloadSize = (Get-Item $payloadFile).Length / 1KB
Write-Host "  Client files: $fileCount" -ForegroundColor Green
Write-Host "  Server payload: $([math]::Round($payloadSize, 2)) KB" -ForegroundColor Green

# Clean up
if (Test-Path $tempFolder) {
    Remove-Item -Path $tempFolder -Recurse -Force
}

Write-Host ""
Write-Host "[2/5] Creating package..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null

Write-Host ""
Write-Host "[3/5] Copying files..." -ForegroundColor Yellow
Copy-Item -Path "$sourceFolder\*" -Destination $tempFolder -Recurse -Force
Write-Host "  Copied client files" -ForegroundColor Green

Copy-Item -Path $payloadFile -Destination $tempFolder -Force
Write-Host "  Copied server payload" -ForegroundColor Green

# Create detailed changelog
$changelogContent = @"
# PS5 Upload Suite v1.2 - Complete Bug Fix Release

## What's Fixed:

### CLIENT FIXES:

#### Bug #1: Connection Timeout
- **Problem:** Application took 30+ seconds to detect wrong IP address
- **Fix:** Added 5-second connection timeout
- **Result:** Immediate feedback on connection failure

#### Bug #2: Client-Side Timeout
- **Problem:** Client timeout on folder operations
- **Fix:** Increased client timeout to 60 seconds
- **Result:** Better handling of server operations

### SERVER FIXES:

#### Bug #3: Folder Deletion Timeout (MAJOR FIX!)
- **Problem:** Deleting large game folders (10GB+) caused "Read timeout" error
- **Fix:** Folder deletion now runs asynchronously in background thread
- **How it works:**
  1. Server responds immediately: "Deleting folder in background..."
  2. Deletion happens in separate thread
  3. PS5 notification appears when complete
- **Result:** NO MORE TIMEOUT! Works with any folder size!

## Installation:
1. Extract ZIP file
2. Run PS5Upload.exe on Windows
3. Upload ps5_upload_server.elf to PS5 (via FTP or USB)
4. Load ps5_upload_server.elf with etaHEN payload injector
5. Connect and enjoy!

## Upgrading from v1.0/v1.1:
Replace both:
- PS5Upload.exe (Windows client)
- ps5_upload_server.elf (PS5 server)

IMPORTANT: You MUST update BOTH files for the fixes to work!

## Testing the Fixes:
1. **Connection timeout:** Try wrong IP - should fail in 5 seconds
2. **Folder deletion:** Delete large game folder - should work without timeout

## Requirements:
- Windows 10/11 (64-bit)
- PS5 with etaHEN 2.0+ or GoldHEN
- Network connection

## Support:
GitHub: https://github.com/YOUR_USERNAME/ps5_upload_suite
Issues: Report bugs on GitHub Issues page

---
**Version:** 1.2  
**Release Date:** January 12, 2026  
**Build:** Complete Bug Fix Release
"@

Set-Content -Path "$tempFolder\CHANGELOG_v1.2.txt" -Value $changelogContent -Encoding UTF8
Write-Host "  Created CHANGELOG_v1.2.txt" -ForegroundColor Green

# Create quick start guide
$quickStartContent = @"
# PS5 Upload Suite v1.2 - Quick Start

## Step 1: Load Server on PS5
1. Copy ps5_upload_server.elf to USB drive
2. Insert USB into PS5
3. Open etaHEN payload injector
4. Load ps5_upload_server.elf
5. Note the IP address shown on screen

## Step 2: Connect from Windows
1. Run PS5Upload.exe
2. Enter PS5 IP address
3. Click Connect
4. You're ready!

## Step 3: Upload Files
1. Click "Browse Folder" to select files
2. Click "Upload" to transfer
3. Files appear in /data/etaHEN/games on PS5

## Deleting Large Folders:
- Now works perfectly with async deletion!
- Server responds immediately
- Deletion happens in background
- PS5 notification when complete

Enjoy! 🎮
"@

Set-Content -Path "$tempFolder\QUICK_START.txt" -Value $quickStartContent -Encoding UTF8
Write-Host "  Created QUICK_START.txt" -ForegroundColor Green

Write-Host ""
Write-Host "[4/5] Creating ZIP..." -ForegroundColor Yellow

if (Test-Path $outputZip) {
    Remove-Item -Path $outputZip -Force
}

Compress-Archive -Path "$tempFolder\*" -DestinationPath $outputZip -CompressionLevel Optimal -Force

if (Test-Path $outputZip) {
    $zipSize = (Get-Item $outputZip).Length / 1MB
    Write-Host "  Created: $outputZip" -ForegroundColor Green
    Write-Host "  Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
} else {
    Write-Host "[ERROR] Failed to create ZIP!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "[5/5] Cleaning up..." -ForegroundColor Yellow
Remove-Item -Path $tempFolder -Recurse -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "SUCCESS!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Release: $outputZip" -ForegroundColor Green
Write-Host "Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
Write-Host ""
Write-Host "Bug Fixes Applied:" -ForegroundColor Yellow
Write-Host "  [CLIENT] Connection timeout: 5 seconds" -ForegroundColor Green
Write-Host "  [CLIENT] Folder deletion timeout: 60 seconds" -ForegroundColor Green
Write-Host "  [SERVER] Async folder deletion (NO MORE TIMEOUT!)" -ForegroundColor Green
Write-Host ""
Write-Host "IMPORTANT: Update BOTH client and server!" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "1. Test locally" -ForegroundColor White
Write-Host "2. Upload ps5_upload_server.elf to PS5" -ForegroundColor White
Write-Host "3. Load with etaHEN" -ForegroundColor White
Write-Host "4. Test folder deletion with large folder" -ForegroundColor White
Write-Host "5. Upload to GitHub as v1.2" -ForegroundColor White
Write-Host ""
