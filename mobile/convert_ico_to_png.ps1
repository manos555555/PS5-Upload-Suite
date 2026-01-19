Add-Type -AssemblyName System.Drawing

$icoPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\client\ps5_logo_fixed.ico"
$outputPaths = @(
    "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Platforms\Android\Resources\mipmap-xxxhdpi\appicon.png",
    "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Platforms\Android\Resources\mipmap-xxhdpi\appicon.png",
    "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Platforms\Android\Resources\mipmap-xhdpi\appicon.png"
)

$ico = [System.Drawing.Icon]::new($icoPath)
$bmp = $ico.ToBitmap()

foreach ($path in $outputPaths) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Created: $path"
}

$bmp.Dispose()
$ico.Dispose()

Write-Host "Conversion complete!"
