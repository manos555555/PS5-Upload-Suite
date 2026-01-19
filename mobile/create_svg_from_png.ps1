$pngPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Resources\AppIcon\ps5logo.png"
$svgPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Resources\AppIcon\appicon.svg"
$fgPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Resources\AppIcon\appiconfg.svg"

# Read PNG and convert to base64
$bytes = [IO.File]::ReadAllBytes($pngPath)
$base64 = [Convert]::ToBase64String($bytes)

# Create SVG with embedded PNG for background
$svg = @"
<?xml version="1.0" encoding="UTF-8"?>
<svg width="256" height="256" viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
  <image x="0" y="0" width="256" height="256" xlink:href="data:image/png;base64,$base64"/>
</svg>
"@

# Create empty foreground (the logo is already complete)
$fg = @"
<?xml version="1.0" encoding="UTF-8"?>
<svg width="256" height="256" viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg">
</svg>
"@

$svg | Out-File -FilePath $svgPath -Encoding UTF8
$fg | Out-File -FilePath $fgPath -Encoding UTF8

Write-Host "SVG icon created with embedded PNG!"
Write-Host "Base64 length: $($base64.Length)"
