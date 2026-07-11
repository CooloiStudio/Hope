# Hope — 双远端一键推送（Windows PowerShell，无需 make）
#
# 用法：
#   pwsh ./scripts/push-remotes.ps1
#   pwsh ./scripts/push-remotes.ps1 -Force
#   pwsh ./scripts/push-remotes.ps1 -Tag v0.13.90
#   pwsh ./scripts/push-remotes.ps1 -Force -Tag 0.13.90
#   pwsh ./scripts/push-remotes.ps1 -TagOnly -Tag v0.13.90
#
# 参数：
#   -Force    使用 --force-with-lease 覆盖远端分支/标签
#   -Tag      发版标签（可写 v0.13.90 或 0.13.90，自动补 v）
#   -TagOnly  只打/推 tag，不推分支

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Tag = "",
    [switch]$TagOnly
)

$ErrorActionPreference = "Stop"

$Remotes = @("origin", "gitee")
$Branches = @("release", "master", "develop")
$GiteeUrl = "git@gitee.com:CooloiStudio/Hope.git"

function Ensure-GiteeRemote {
    $existing = git remote 2>$null
    if ($existing -notcontains "gitee") {
        Write-Host "==> add remote gitee $GiteeUrl"
        git remote add gitee $GiteeUrl
    }
}

function Normalize-Tag([string]$raw) {
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    if ($raw.StartsWith("v")) { return $raw }
    return "v$raw"
}

function Push-Branches {
    $flags = @()
    if ($Force) { $flags += "--force-with-lease" }

    foreach ($remote in $Remotes) {
        foreach ($branch in $Branches) {
            Write-Host "==> git push $($flags -join ' ') $remote $branch"
            if ($flags.Count -gt 0) {
                git push @flags $remote $branch
            } else {
                git push $remote $branch
            }
            if ($LASTEXITCODE -ne 0) { throw "push failed: $remote $branch" }
        }
    }
}

function Push-Tag([string]$tagName) {
    $tagArgs = @()
    if ($Force) { $tagArgs += "-f" }
    Write-Host "==> git tag $($tagArgs -join ' ') $tagName"
    if ($tagArgs.Count -gt 0) {
        git tag @tagArgs $tagName
    } else {
        git tag $tagName
    }
    if ($LASTEXITCODE -ne 0) { throw "git tag failed: $tagName" }

    # tag 强制推送用 --force：--force-with-lease 对 tag 常因缺少远端跟踪而报 stale info
    foreach ($remote in $Remotes) {
        if ($Force) {
            Write-Host "==> git push --force $remote $tagName"
            git push --force $remote $tagName
        } else {
            Write-Host "==> git push $remote $tagName"
            git push $remote $tagName
        }
        if ($LASTEXITCODE -ne 0) { throw "push tag failed: $remote $tagName" }
    }
}

Ensure-GiteeRemote
Write-Host "==> Force=$Force Tag=$Tag TagOnly=$TagOnly"

if (-not $TagOnly) {
    Push-Branches
}

$normalized = Normalize-Tag $Tag
if ($TagOnly -and -not $normalized) {
    throw "TagOnly 需要同时指定 -Tag（例如 -Tag v0.13.90）"
}
if ($normalized) {
    Push-Tag $normalized
}

Write-Host "==> done"
