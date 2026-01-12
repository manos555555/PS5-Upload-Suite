# PS5 Upload Suite v1.1 - Bug Fix Release Package Creator

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "PS5 Upload Suite v1.1 - Bug Fix Release" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$sourceFolder = ".\client\bin\Release\net6.0-windows\win-x64"
$payloadFile = ".\payload\ps5_upload_server.elf"
$outputZip = ".\PS5Upload-Windows-x64-v1.1-BugFix.zip"
$tempFolder = ".\github_release_v1.1_temp"

Write-Host "[INFO] Bug Fixes in v1.1:" -ForegroundColor Yellow
Write-Host "  1. Connection timeout: 5 seconds (fast fail on wrong IP)" -ForegroundColor Green
Write-Host "  2. Folder deletion timeout: 60 seconds (handles large folders)" -ForegroundColor Green
Write-Host ""

# Check if source folder exists
if (-not (Test-Path $sourceFolder)) {
    Write-Host "[ERROR] Source folder not found: $sourceFolder" -ForegroundColor Red
    exit 1
}

Write-Host "[1/5] Checking source files..." -ForegroundColor Yellow
$fileCount = (Get-ChildItem -Path $sourceFolder -File).Count
Write-Host "  Found $fileCount files" -ForegroundColor Green

# Clean up old temp folder
if (Test-Path $tempFolder) {
    Remove-Item -Path $tempFolder -Recurse -Force
}

Write-Host ""
Write-Host "[2/5] Creating temporary folder..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null

Write-Host ""
Write-Host "[3/5] Copying files..." -ForegroundColor Yellow
Copy-Item -Path "$sourceFolder\*" -Destination $tempFolder -Recurse -Force
Write-Host "  Copied $fileCount files" -ForegroundColor Green

if (Test-Path $payloadFile) {
    Copy-Item -Path $payloadFile -Destination $tempFolder -Force
    Write-Host "  Copied ps5_upload_server.elf" -ForegroundColor Green
}

# Create CHANGELOG
$changelogContent = @"
# PS5 Upload Suite v1.1 - Bug Fix Release

## What's Fixed:

### Bug #1: Connection Timeout
- **Problem:** Application took 30+ seconds to detect wrong IP address
- **Fix:** Added 5-second timeout for connection attempts
- **Result:** Fast failure on incorrect IP - immediate feedback

### Bug #2: Folder Deletion Timeout  
- **Problem:** Deleting large game folders caused timeout errors
- **Fix:** Increased timeout to 60 seconds for folder operations
- **Result:** Successfully handles large folders (10GB+ games)

## Installation:
Same as v1.0 - extract and run PS5Upload.exe

## Upgrading from v1.0:
Simply replace your old PS5Upload.exe with the new one.
No other changes needed.

## Requirements:
- Windows 10/11 (64-bit)
- PS5 with etaHEN 2.0+ or GoldHEN
- Network connection

## Support:
GitHub: https://github.com/YOUR_USERNAME/ps5_upload_suite
"@

Set-Content -Path "$tempFolder\CHANGELOG.txt" -Value $changelogContent -Encoding UTF8
Write-Host "  Created CHANGELOG.txt" -ForegroundColor Green

Write-Host ""
Write-Host "[4/5] Creating ZIP archive..." -ForegroundColor Yellow

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
Write-Host "Release package created: $outputZip" -ForegroundColor Green
Write-Host "Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
Write-Host ""
Write-Host "Bug Fixes:" -ForegroundColor Yellow
Write-Host "  [FIXED] Connection timeout: 5 seconds" -ForegroundColor Green
Write-Host "  [FIXED] Folder deletion timeout: 60 seconds" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Test locally: Extract and run PS5Upload.exe" -ForegroundColor White
Write-Host "2. Upload to GitHub as v1.1 release" -ForegroundColor White
Write-Host "3. Tag: v1.1" -ForegroundColor White
Write-Host ""
