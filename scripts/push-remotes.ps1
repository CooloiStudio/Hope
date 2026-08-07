# Hope — 双远端一键推送（Windows PowerShell，无需 make）
#
# 仓库长驻分支仅 master；发版靠注解 tag。
#
# 用法：
#   pwsh ./scripts/push-remotes.ps1                      # 推 master
#   pwsh ./scripts/push-remotes.ps1 -Force
#   pwsh ./scripts/push-remotes.ps1 -Tag 0.13.90         # 先推 master，再打 tag（已存在则覆盖）
#   pwsh ./scripts/push-remotes.ps1 -TagOnly -Tag v0.13.90  # 仅推 tag（不推分支）
#
# 参数：
#   -Force    分支用 --force-with-lease
#   -Tag      发版标签（可写 v0.13.90 或 0.13.90，自动补 v）；本地/远端已存在则删远端后覆盖到当前 HEAD
#   -TagOnly  只推 tag，不推 master（默认会推 master）

[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Tag = "",
    [switch]$TagOnly
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
    $tagCommit = git rev-list -n 1 $tagName 2>$null
    $tagExists = ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($tagCommit))

    # 始终落在当前 HEAD：已存在则 -f 覆盖本地 tag。
    if ($tagExists) {
        Write-Host "==> git tag -f $tagName (retarget $($tagCommit.Substring(0, [Math]::Min(7, $tagCommit.Length))) → HEAD)"
        git tag -f $tagName
    }
    else {
        Write-Host "==> git tag $tagName"
        git tag $tagName
    }
    if ($LASTEXITCODE -ne 0) { throw "git tag failed: $tagName" }

    foreach ($remote in $Remotes) {
        # 先删远端同名 tag（不存在时允许失败），再推送当前本地 tag。
        Write-Host "==> git push $remote :refs/tags/$tagName (delete remote tag if any)"
        git push $remote ":refs/tags/$tagName" 2>&1 | Out-Host
        Write-Host "==> git push $remote $tagName"
        git push $remote $tagName
        if ($LASTEXITCODE -ne 0) { throw "push tag failed: $remote $tagName" }
    }
}

Ensure-GiteeRemote

$normalized = Normalize-Tag $Tag
if ($TagOnly -and -not $normalized) {
    throw "TagOnly requires -Tag (e.g. -Tag v0.13.90)"
}

# 默认推 master；仅 -TagOnly 时跳过分支。
$pushBranches = -not $TagOnly

Write-Host "==> Force=$Force Tag=$Tag TagOnly=$TagOnly -> pushBranches=$pushBranches (master only)"

if ($pushBranches) {
    Push-Branches
}

if ($normalized) {
    Push-Tag $normalized
} elseif (-not $pushBranches) {
    throw "Nothing to push: pass -Tag, or omit -TagOnly to push master"
}

Write-Host "==> done"
