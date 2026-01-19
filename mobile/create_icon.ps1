$svg = @'
<?xml version="1.0" encoding="UTF-8"?>
<svg width="456" height="456" viewBox="0 0 456 456" xmlns="http://www.w3.org/2000/svg">
  <rect x="0" y="0" width="456" height="456" fill="#003087" rx="50"/>
  <text x="228" y="200" font-family="Arial" font-size="100" font-weight="bold" fill="white" text-anchor="middle">PS5</text>
  <text x="228" y="320" font-family="Arial" font-size="50" fill="white" text-anchor="middle">Upload</text>
</svg>
'@

$svg | Out-File -FilePath "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Resources\AppIcon\appicon.svg" -Encoding UTF8
Write-Host "SVG icon created successfully!"
