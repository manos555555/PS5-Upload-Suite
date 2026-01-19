$pngPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\client\ps5_logo.png"
$svgPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Resources\AppIcon\appicon.svg"

# Read PNG and convert to base64
$bytes = [IO.File]::ReadAllBytes($pngPath)
$base64 = [Convert]::ToBase64String($bytes)

# Create SVG with embedded PNG
$svg = @"
<?xml version="1.0" encoding="UTF-8"?>
<svg width="456" height="456" viewBox="0 0 456 456" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
  <image x="0" y="0" width="456" height="456" preserveAspectRatio="xMidYMid meet" xlink:href="data:image/png;base64,$base64" />
</svg>
"@

# Write SVG file
[IO.File]::WriteAllText($svgPath, $svg)

Write-Host "Icon converted successfully!"
Write-Host "SVG size: $($svg.Length) bytes"
