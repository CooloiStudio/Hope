# Hope — one-click unit tests (Windows PowerShell 5.1+ / pwsh)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
#   pwsh ./scripts/test.ps1
#   .\scripts\test.ps1 -DesktopOnly
#   .\scripts\test.ps1 -HeadlessOnly
#   .\scripts\test.ps1 -Configuration Debug
#
# Coverage:
#   - headless: go test ./... (screenSize / settings merge / getSettings+listTasks, ...)
#   - desktop:  EditableTimeCombo hang gate, Session hydrate, IPC dispatch, Fatal-once, sha256, ...

[CmdletBinding()]
param(
    [switch]$DesktopOnly,
    [switch]$HeadlessOnly,
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $Root "src"))) {
    throw "Cannot locate repo root (expected $Root\src)"
}

function Find-DotNet {
    $candidates = @(
        (Join-Path $env:ProgramFiles "dotnet\dotnet.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "dotnet\dotnet.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "dotnet not found. Install .NET SDK 10."
}

function Find-Go {
    $cmd = Get-Command go -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:ProgramFiles "Go\bin\go.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Go\bin\go.exe"),
        (Join-Path $env:USERPROFILE ".g\go\bin\go.exe"),
        (Join-Path $env:USERPROFILE "sdk\go\bin\go.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    throw "go not found. Install Go 1.26+."
}

# Prefer machine/user PATH so non-interactive shells still find go/dotnet.
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
    [System.Environment]::GetEnvironmentVariable("Path", "User") + ";" + $env:Path

$failed = $false

if (-not $DesktopOnly) {
    Write-Host ""
    Write-Host "==> headless: go test ./..." -ForegroundColor Cyan
    $go = Find-Go
    Push-Location (Join-Path $Root "src\headless")
    try {
        & $go test ./...
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }
    finally { Pop-Location }
}

if (-not $HeadlessOnly) {
    Write-Host ""
    Write-Host "==> desktop: dotnet test ($Configuration)" -ForegroundColor Cyan
    $dotnet = Find-DotNet
    $proj = Join-Path $Root "src\win-desktop\tests\Hope.Desktop.Tests.csproj"
    & $dotnet test $proj -c $Configuration --verbosity minimal
    if ($LASTEXITCODE -ne 0) { $failed = $true }
}

Write-Host ""
if ($failed) {
    Write-Host "==> FAIL" -ForegroundColor Red
    exit 1
}
Write-Host "==> PASS" -ForegroundColor Green
exit 0
