#!/usr/bin/env python3
"""
GitHub Release Uploader for PS5 Upload Suite
Uploads v1.2 release to GitHub using GitHub API
"""

import os
import sys
import json
import requests
from pathlib import Path

# GitHub Configuration
GITHUB_TOKEN = "YOUR_GITHUB_TOKEN_HERE"  # Replace with your token
GITHUB_REPO = "manos555555/PS5-Upload-Suite"
RELEASE_TAG = "v1.2"
RELEASE_NAME = "PS5 Upload Suite v1.2 - Complete Bug Fix"
RELEASE_BODY = """# PS5 Upload Suite v1.2 - Complete Bug Fix Release

## 🐛 What's Fixed:

### CLIENT FIXES:
- **Bug #1:** Connection timeout reduced to 5 seconds (fast fail on wrong IP)
- **Bug #2:** Folder deletion timeout increased to 60 seconds

### SERVER FIXES:
- **Bug #3:** Async folder deletion - NO MORE TIMEOUT on large folders! ✅
  - Server responds immediately
  - Deletion happens in background thread
  - PS5 notification when complete

## 📦 What's Included:
- `PS5Upload.exe` - Windows client with bug fixes
- `ps5_upload_server.elf` - PS5 server with async deletion
- All dependencies included
- Self-contained (no .NET installation required)

## 🚀 Installation:
1. Extract ZIP file
2. Run `PS5Upload.exe` on Windows
3. Upload `ps5_upload_server.elf` to PS5 via FTP
4. Load with etaHEN payload injector
5. Connect and enjoy!

## ⚠️ IMPORTANT:
**You MUST update BOTH files:**
- Windows client: `PS5Upload.exe`
- PS5 server: `ps5_upload_server.elf`

## 💡 Testing:
- **Connection timeout:** Try wrong IP - fails in 5 seconds
- **Folder deletion:** Delete large game folder - works without timeout!

## 📋 Requirements:
- Windows 10/11 (64-bit)
- PS5 with etaHEN 2.0+ or GoldHEN
- Network connection

## 🔗 Links:
- [Report Issues](https://github.com/manos555555/PS5-Upload-Suite/issues)
- [Documentation](https://github.com/manos555555/PS5-Upload-Suite/blob/main/README.md)

---
**Version:** 1.2  
**Release Date:** January 12, 2026  
**Build:** Complete Bug Fix Release
"""

def create_github_release(token, repo, tag, name, body):
    """Create a GitHub release"""
    
    url = f"https://api.github.com/repos/{repo}/releases"
    headers = {
        "Authorization": f"token {token}",
        "Accept": "application/vnd.github.v3+json"
    }
    
    data = {
        "tag_name": tag,
        "name": name,
        "body": body,
        "draft": False,
        "prerelease": False
    }
    
    print(f"Creating release {tag}...")
    response = requests.post(url, headers=headers, json=data)
    
    if response.status_code == 201:
        print(f"✅ Release created successfully!")
        return response.json()
    else:
        print(f"❌ Failed to create release: {response.status_code}")
        print(f"Response: {response.text}")
        return None

def upload_release_asset(token, upload_url, file_path):
    """Upload a file to GitHub release"""
    
    # Remove the {?name,label} template from upload_url
    upload_url = upload_url.split("{")[0]
    
    file_name = os.path.basename(file_path)
    file_size = os.path.getsize(file_path) / (1024 * 1024)  # MB
    
    print(f"\nUploading {file_name} ({file_size:.2f} MB)...")
    
    headers = {
        "Authorization": f"token {token}",
        "Content-Type": "application/zip"
    }
    
    params = {"name": file_name}
    
    with open(file_path, 'rb') as f:
        response = requests.post(
            upload_url,
            headers=headers,
            params=params,
            data=f
        )
    
    if response.status_code == 201:
        print(f"✅ {file_name} uploaded successfully!")
        return True
    else:
        print(f"❌ Failed to upload {file_name}: {response.status_code}")
        print(f"Response: {response.text}")
        return False

def main():
    print("=" * 70)
    print("PS5 Upload Suite - GitHub Release Uploader")
    print("=" * 70)
    print()
    
    # Check token
    if GITHUB_TOKEN == "YOUR_GITHUB_TOKEN_HERE":
        print("❌ ERROR: Please set your GitHub token in the script!")
        print()
        print("To get a token:")
        print("1. Go to: https://github.com/settings/tokens")
        print("2. Generate new token (classic)")
        print("3. Select 'repo' scope")
        print("4. Copy token and paste in this script")
        return 1
    
    # Check if release file exists
    release_file = Path("PS5Upload-Windows-x64-v1.2-Complete.zip")
    if not release_file.exists():
        print(f"❌ ERROR: Release file not found: {release_file}")
        return 1
    
    print(f"📦 Release file: {release_file}")
    print(f"📊 Size: {release_file.stat().st_size / (1024*1024):.2f} MB")
    print()
    
    # Create release
    release_data = create_github_release(
        GITHUB_TOKEN,
        GITHUB_REPO,
        RELEASE_TAG,
        RELEASE_NAME,
        RELEASE_BODY
    )
    
    if not release_data:
        return 1
    
    # Upload asset
    upload_url = release_data.get("upload_url")
    if not upload_url:
        print("❌ ERROR: No upload URL in release response")
        return 1
    
    success = upload_release_asset(
        GITHUB_TOKEN,
        upload_url,
        str(release_file)
    )
    
    if success:
        print()
        print("=" * 70)
        print("✅ SUCCESS! Release published to GitHub")
        print("=" * 70)
        print()
        print(f"🔗 Release URL: {release_data.get('html_url')}")
        print()
        return 0
    else:
        return 1

if __name__ == "__main__":
    sys.exit(main())
