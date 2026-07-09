# 将 stage/ 编译产物打成 MSIX（微软商店 Package URL 用，x64）。
# 用法：pwsh scripts/pack-msix.ps1 -StageDir stage -OutputDir dist -Version 0.13.83
#
# 可选环境变量（须与 Partner Center → 产品身份 一致）：
#   HOPE_MSIX_IDENTITY_NAME  包身份 Name（默认 CooloiStudio.Hope）
#   HOPE_MSIX_PUBLISHER      发布者 DN（默认占位，上架前请在 CI Secrets 中配置真实值）

param(
    [Parameter(Mandatory = $true)][string]$StageDir,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $StageDir)) { throw "StageDir 不存在: $StageDir" }
if (-not (Test-Path "$StageDir\hope-desktop.exe")) { throw "未找到 hope-desktop.exe" }
if (-not (Test-Path "$StageDir\hope-headless.exe")) { throw "未找到 hope-headless.exe" }

$packageName = if ($env:HOPE_MSIX_IDENTITY_NAME) { $env:HOPE_MSIX_IDENTITY_NAME } else { "CooloiStudio.Hope" }
$publisher = if ($env:HOPE_MSIX_PUBLISHER) { $env:HOPE_MSIX_PUBLISHER } else {
    "CN=00000000-0000-0000-0000-000000000000"
}

# MSIX 版本须为四段式：0.13.83 → 0.13.83.0
$parts = $Version.Split(".")
while ($parts.Count -lt 4) { $parts += "0" }
$packageVersion = ($parts[0..3] -join ".")

$repoRoot = Split-Path $PSScriptRoot -Parent
$templatePath = Join-Path $repoRoot "packaging\AppxManifest.template.xml"
$iconPath = Join-Path $repoRoot "src\resources\hope.png"
if (-not (Test-Path $templatePath)) { throw "未找到 $templatePath" }
if (-not (Test-Path $iconPath)) { throw "未找到图标 $iconPath" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$layout = Join-Path $env:TEMP "hope-msix-layout-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force -Path $layout | Out-Null

try {
    # 复制编译产物到布局根目录
    Copy-Item -Path (Join-Path $StageDir "*") -Destination $layout -Recurse -Force

    # 生成商店图标资源
    $assetsDir = Join-Path $layout "Assets"
    New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile((Resolve-Path $iconPath))
    try {
        foreach ($entry in @(
            @{ File = "StoreLogo.png"; Size = 50 },
            @{ File = "Square44x44Logo.png"; Size = 44 },
            @{ File = "Square150x150Logo.png"; Size = 150 }
        )) {
            $bmp = New-Object System.Drawing.Bitmap $entry.Size, $entry.Size
            try {
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                try {
                    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $g.Clear([System.Drawing.Color]::Transparent)
                    $g.DrawImage($src, 0, 0, $entry.Size, $entry.Size)
                } finally { $g.Dispose() }
                $bmp.Save((Join-Path $assetsDir $entry.File), [System.Drawing.Imaging.ImageFormat]::Png)
            } finally { $bmp.Dispose() }
        }
    } finally { $src.Dispose() }

    # 生成 AppxManifest.xml
    $manifest = Get-Content $templatePath -Raw -Encoding UTF8
    $manifest = $manifest.Replace("{{PACKAGE_NAME}}", [System.Security.SecurityElement]::Escape($packageName))
    $manifest = $manifest.Replace("{{PUBLISHER}}", [System.Security.SecurityElement]::Escape($publisher))
    $manifest = $manifest.Replace("{{PACKAGE_VERSION}}", $packageVersion)
    $manifestPath = Join-Path $layout "AppxManifest.xml"
    [System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding $false))

    # Locate MakeAppx.exe (Windows SDK)
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        $programFilesX86 = "C:\Program Files (x86)"
    }
    $kitsRoot = Join-Path -Path $programFilesX86 -ChildPath "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kitsRoot)) {
        throw "Windows SDK not found: $kitsRoot"
    }
    $makeAppx = Get-ChildItem -Path $kitsRoot -Recurse -Filter "makeappx.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\makeappx\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $makeAppx) { throw "未找到 makeappx.exe，请安装 Windows 10 SDK" }

    $safeVersion = $Version.Replace(".", "_")
    $msixName = "Hope_${safeVersion}_x64.msix"
    $msixPath = Join-Path $OutputDir $msixName

    Write-Host "MakeAppx: $($makeAppx.FullName)"
    Write-Host "Package: $packageName  Version: $packageVersion  Publisher: $publisher"
    & $makeAppx.FullName pack /d $layout /p $msixPath /o | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败，退出码 $LASTEXITCODE" }
    if (-not (Test-Path $msixPath)) { throw "未生成 $msixPath" }

    # .msixupload：供 Partner Center「Package URL」或手动上传（内含 msix + 清单副本）
    $uploadName = "Hope_${safeVersion}_x64.msixupload"
    $uploadPath = Join-Path $OutputDir $uploadName
    $uploadStaging = Join-Path $env:TEMP "hope-msixupload-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $uploadStaging | Out-Null
    Copy-Item $msixPath (Join-Path $uploadStaging $msixName)
    Copy-Item $manifestPath (Join-Path $uploadStaging "AppxManifest.xml")
    if (Test-Path $uploadPath) { Remove-Item $uploadPath -Force }
    Compress-Archive -Path (Join-Path $uploadStaging "*") -DestinationPath $uploadPath -Force
    Remove-Item $uploadStaging -Recurse -Force

    $hash = (Get-FileHash -Algorithm SHA256 -Path $msixPath).Hash.ToLower()
    "$hash  $msixName" | Set-Content -Path "$msixPath.sha256" -Encoding ascii -NoNewline

    Write-Host "MSIX: $msixPath"
    Write-Host "MSIXUPLOAD: $uploadPath"
    Write-Host "SHA256: $hash"
} finally {
    if (-not [string]::IsNullOrWhiteSpace($layout) -and (Test-Path -LiteralPath $layout)) {
        Remove-Item -LiteralPath $layout -Recurse -Force -ErrorAction SilentlyContinue
    }
}
