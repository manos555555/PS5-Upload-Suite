# PS5 Upload Suite - GitHub Release Package Creator
# Creates a clean, ready-to-upload ZIP file for GitHub releases

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "PS5 Upload Suite - Release Packager" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Configuration
$sourceFolder = ".\client\bin\Release\net6.0-windows\win-x64"
$payloadFile = ".\payload\ps5_upload_server.elf"
$outputZip = ".\PS5Upload-Windows-x64-v1.0.zip"
$tempFolder = ".\github_release_temp"

# Check if source folder exists
if (-not (Test-Path $sourceFolder)) {
    Write-Host "[ERROR] Source folder not found: $sourceFolder" -ForegroundColor Red
    Write-Host "Please build the project first!" -ForegroundColor Yellow
    exit 1
}

Write-Host "[1/5] Checking source files..." -ForegroundColor Yellow

# Count files in source
$fileCount = (Get-ChildItem -Path $sourceFolder -File).Count
Write-Host "  Found $fileCount files in source folder" -ForegroundColor Green

# Check for main executable
$mainExe = Join-Path $sourceFolder "PS5Upload.exe"
if (-not (Test-Path $mainExe)) {
    Write-Host "[ERROR] PS5Upload.exe not found!" -ForegroundColor Red
    exit 1
}

$exeSize = (Get-Item $mainExe).Length / 1MB
Write-Host "  PS5Upload.exe: $([math]::Round($exeSize, 2)) MB" -ForegroundColor Green

# Check for payload
if (Test-Path $payloadFile) {
    $payloadSize = (Get-Item $payloadFile).Length / 1KB
    Write-Host "  ps5_upload_server.elf: $([math]::Round($payloadSize, 2)) KB" -ForegroundColor Green
} else {
    Write-Host "  [WARN] ps5_upload_server.elf not found - will skip" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "[2/5] Creating temporary folder..." -ForegroundColor Yellow

# Clean up old temp folder
if (Test-Path $tempFolder) {
    Remove-Item -Path $tempFolder -Recurse -Force
}

# Create temp folder
New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null
Write-Host "  Created: $tempFolder" -ForegroundColor Green

Write-Host ""
Write-Host "[3/5] Copying files..." -ForegroundColor Yellow

# Copy all files from source
Copy-Item -Path "$sourceFolder\*" -Destination $tempFolder -Recurse -Force
Write-Host "  Copied $fileCount files from win-x64 build" -ForegroundColor Green

# Copy payload if exists
if (Test-Path $payloadFile) {
    Copy-Item -Path $payloadFile -Destination $tempFolder -Force
    Write-Host "  Copied ps5_upload_server.elf" -ForegroundColor Green
}

# Create README for the release
$readmeContent = @"
# PS5 Upload Suite v1.0 - Windows x64

## What's This?
Self-contained Windows application for uploading files to PS5 console via network.

## Requirements
- Windows 10/11 (64-bit)
- PS5 with etaHEN 2.0+ or GoldHEN
- Network connection between PC and PS5

## Installation
1. Extract all files to a folder
2. Run PS5Upload.exe

## First Time Setup
1. Load ps5_upload_server.elf to your PS5 (via etaHEN payload injector)
2. Note the IP address shown on PS5 screen
3. Open PS5Upload.exe on Windows
4. Enter PS5 IP address
5. Click Connect

## Usage
1. Click "Browse" to select files
2. Click "Upload" to transfer to PS5
3. Files will be saved to /data/ on PS5

## Troubleshooting
- **Connection failed:** Make sure PS5 server is running and IP is correct
- **Upload failed:** Check PS5 has enough free space
- **App won't start:** Make sure all files are extracted

## Support
GitHub: https://github.com/YOUR_USERNAME/ps5_upload_suite
Issues: https://github.com/YOUR_USERNAME/ps5_upload_suite/issues

## License
MIT License - See repository for details
"@

Set-Content -Path "$tempFolder\README.txt" -Value $readmeContent -Encoding UTF8
Write-Host "  Created README.txt" -ForegroundColor Green

Write-Host ""
Write-Host "[4/5] Creating ZIP archive..." -ForegroundColor Yellow

# Remove old ZIP if exists
if (Test-Path $outputZip) {
    Remove-Item -Path $outputZip -Force
    Write-Host "  Removed old ZIP file" -ForegroundColor Yellow
}

# Create ZIP
Compress-Archive -Path "$tempFolder\*" -DestinationPath $outputZip -CompressionLevel Optimal -Force

if (Test-Path $outputZip) {
    $zipSize = (Get-Item $outputZip).Length / 1MB
    Write-Host "  Created: $outputZip" -ForegroundColor Green
    Write-Host "  Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
} else {
    Write-Host "[ERROR] Failed to create ZIP file!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "[5/5] Cleaning up..." -ForegroundColor Yellow

# Remove temp folder
Remove-Item -Path $tempFolder -Recurse -Force
Write-Host "  Removed temporary files" -ForegroundColor Green

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "SUCCESS!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Release package created: $outputZip" -ForegroundColor Green
Write-Host "Size: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Test the ZIP file locally (extract and run PS5Upload.exe)" -ForegroundColor White
Write-Host "2. Go to GitHub: https://github.com/YOUR_USERNAME/ps5_upload_suite/releases/new" -ForegroundColor White
Write-Host "3. Create new release with tag 'v1.0'" -ForegroundColor White
Write-Host "4. Upload: $outputZip" -ForegroundColor White
Write-Host "5. Publish release" -ForegroundColor White
Write-Host ""
Write-Host "Press any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
