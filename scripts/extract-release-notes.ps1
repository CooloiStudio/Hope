# Extract release notes for a desktop version from CHANGELOG.md only.
#
# Usage:
#   pwsh ./scripts/extract-release-notes.ps1 -Version 0.14.93 -OutFile release-notes.md
#
# Client shows GitHub Release body as plain text; keep ### vX.Y.Z bullets readable without Markdown.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutFile = "release-notes.md",
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

function ConvertTo-PlainNotes([string]$body) {
    # Light cleanup so Release body is readable in the desktop TextBox.
    $s = $body
    $s = [regex]::Replace($s, '\[([^\]]+)\]\([^)]+\)', '$1') # [label](url) -> label
    $s = [regex]::Replace($s, '\*\*([^*]+)\*\*', '$1')         # **bold**
    $s = [regex]::Replace($s, '`([^`]+)`', '$1')               # `code`
    return $s.Trim()
}

$md = Join-Path $RepoRoot "CHANGELOG.md"
if (-not (Test-Path -LiteralPath $md)) {
    Write-Host "CHANGELOG.md not found"
    Write-Output "found=false"
    exit 0
}

$lines = Get-Content -LiteralPath $md -Encoding utf8
$pattern = '^###\s+v' + [regex]::Escape($Version) + '(\s|\(|$)'
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $pattern) { $start = $i; break }
}

if ($start -lt 0) {
    Write-Host "No ### v$Version section in CHANGELOG.md"
    Write-Output "found=false"
    exit 0
}

$end = $lines.Count
for ($j = $start + 1; $j -lt $lines.Count; $j++) {
    if ($lines[$j] -match '^(##|###)\s') { $end = $j; break }
}

$body = if ($end -gt $start + 1) {
    ($lines[($start + 1)..($end - 1)] -join "`n").Trim()
} else { "" }

if ([string]::IsNullOrWhiteSpace($body)) {
    Write-Host "Empty notes under ### v$Version"
    Write-Output "found=false"
    exit 0
}

$plain = ConvertTo-PlainNotes $body
$outPath = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $RepoRoot $OutFile }
Set-Content -Path $outPath -Value $plain -Encoding utf8
Write-Host "Extracted CHANGELOG.md v$Version -> $outPath"
Write-Output "found=true"
exit 0
