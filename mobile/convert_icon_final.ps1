Add-Type -AssemblyName System.Drawing

$icoPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\client\ps5_logo_fixed.ico"
$pngPath = "C:\Users\HACKMAN\Desktop\ps5 test\my_projects\ps5_upload_suite\mobile\Resources\AppIcon\ps5logo.png"

$ico = [System.Drawing.Icon]::new($icoPath)
$bmp = $ico.ToBitmap()
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host "PNG created: $pngPath"
Write-Host "Size: $($bmp.Width)x$($bmp.Height)"

$bmp.Dispose()
$ico.Dispose()
