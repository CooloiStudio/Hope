Add-Type -AssemblyName System.Drawing
$src = Join-Path $PSScriptRoot "..\src\resources\hope.png"
$dst = Join-Path $PSScriptRoot "..\src\resources\hope.ico"
$bmp = [System.Drawing.Bitmap]::FromFile($src)
$resized = New-Object System.Drawing.Bitmap $bmp, 32, 32
$icon = [System.Drawing.Icon]::FromHandle($resized.GetHicon())
$fs = [System.IO.File]::Open($dst, [System.IO.FileMode]::Create)
$icon.Save($fs)
$fs.Close()
$icon.Dispose()
$resized.Dispose()
$bmp.Dispose()
Write-Host "Created $dst"
