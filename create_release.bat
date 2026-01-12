@echo off
cd /d "%~dp0"
"C:\Program Files\GitHub CLI\gh.exe" release create v1.2 PS5Upload-Windows-x64-v1.2-Complete.zip --title "PS5 Upload Suite v1.2 - Complete Bug Fix" --notes "# PS5 Upload Suite v1.2 - Complete Bug Fix Release

## What's Fixed:

### CLIENT FIXES:
- Bug #1: Connection timeout reduced to 5 seconds (fast fail on wrong IP)
- Bug #2: Folder deletion timeout increased to 60 seconds

### SERVER FIXES:
- Bug #3: Async folder deletion - NO MORE TIMEOUT on large folders!
  - Server responds immediately
  - Deletion happens in background thread
  - PS5 notification when complete

## What's Included:
- PS5Upload.exe - Windows client with bug fixes
- ps5_upload_server.elf - PS5 server with async deletion
- All dependencies included

## Installation:
1. Extract ZIP file
2. Run PS5Upload.exe on Windows
3. Upload ps5_upload_server.elf to PS5 via FTP
4. Load with etaHEN payload injector

## IMPORTANT:
You MUST update BOTH files (client and server)

## Requirements:
- Windows 10/11 (64-bit)
- PS5 with etaHEN 2.0+ or GoldHEN"
