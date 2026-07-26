# Hope — 双远端一键推送（Windows PowerShell，无需 make）
#
# 仓库长驻分支仅 master；发版靠注解 tag。
#
# 用法：
#   pwsh ./scripts/push-remotes.ps1                         # 推 master
#   pwsh ./scripts/push-remotes.ps1 -Force
#   pwsh ./scripts/push-remotes.ps1 -Force -Tag 0.13.90     # 只推 tag → 仅触发 release
#   pwsh ./scripts/push-remotes.ps1 -TagOnly -Tag v0.13.90  # 同上（兼容旧用法）
#   pwsh ./scripts/push-remotes.ps1 -Force -Tag 0.13.90 -AlsoPushBranches  # master + tag
#
# 参数：
#   -Force             分支用 --force-with-lease；tag 用 --force
#   -Tag               发版标签（可写 v0.13.90 或 0.13.90，自动补 v）
#                      一旦指定 -Tag，默认只推该 tag，不推分支（避免连带触发 ci）
#   -TagOnly           兼容开关：与「只指定 -Tag」行为相同
#   -AlsoPushBranches  与 -Tag 联用时，额外推送 master

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Tag = "",
    [switch]$TagOnly,
    [switch]$AlsoPushBranches
)

$ErrorActionPreference = "Stop"

$Remotes = @("origin", "gitee")
$Branches = @("master")
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

    # tag 强制推送用 --force：--force-with-lease 对 tag 常因缺少期望跟踪而报 stale info
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

$normalized = Normalize-Tag $Tag
if ($TagOnly -and -not $normalized) {
    throw "TagOnly 需要同时指定 -Tag（例如 -Tag v0.13.90）"
}

# 指定了 -Tag：默认只推 tag（仅触发 release.yml）；要连带推 master 需 -AlsoPushBranches。
# 未指定 -Tag：推 master（触发 ci.yml）。
$pushBranches = if ($normalized) { [bool]$AlsoPushBranches } else { -not $TagOnly }

Write-Host "==> Force=$Force Tag=$Tag TagOnly=$TagOnly AlsoPushBranches=$AlsoPushBranches → pushBranches=$pushBranches (master only)"

if ($pushBranches) {
    Push-Branches
}

if ($normalized) {
    Push-Tag $normalized
} elseif (-not $pushBranches) {
    throw "没有可推送的内容：请指定 -Tag，或去掉 -TagOnly 以推送 master"
}

Write-Host "==> done"
