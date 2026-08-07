#!/usr/bin/env bash
# Hope — 双远端一键推送（Git Bash，无需 make）
#
# 仓库长驻分支仅 master；发版靠注解 tag。
#
# 用法：
#   ./scripts/push-remotes.sh
#   ./scripts/push-remotes.sh --force
#   ./scripts/push-remotes.sh --tag 0.13.90              # 先推 master，再打 tag（已存在则覆盖）
#   ./scripts/push-remotes.sh --tag-only --tag v0.13.90  # 仅推 tag（不推分支）
#
# --tag：本地/远端已存在同名 tag 时，删远端后覆盖到当前 HEAD。

set -euo pipefail

REMOTES=(origin gitee)
BRANCHES=(master)
GITEE_URL="git@gitee.com:CooloiStudio/Hope.git"

FORCE=0
TAG=""
TAG_ONLY=0

usage() {
  echo "Usage: $0 [--force] [--tag vX.Y.Z] [--tag-only]"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force|-f) FORCE=1; shift ;;
    --tag|-t)
      [[ $# -ge 2 ]] || { echo "ERROR: --tag needs a value"; exit 1; }
      TAG="$2"; shift 2 ;;
    --tag-only) TAG_ONLY=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown arg: $1"; usage; exit 1 ;;
  esac
done

ensure_gitee() {
  if ! git remote get-url gitee >/dev/null 2>&1; then
    echo "==> add remote gitee $GITEE_URL"
    git remote add gitee "$GITEE_URL"
  fi
}

normalize_tag() {
  local raw="$1"
  [[ -z "$raw" ]] && { echo ""; return; }
  if [[ "$raw" == v* ]]; then echo "$raw"; else echo "v$raw"; fi
}

ensure_gitee

tag="$(normalize_tag "$TAG")"
if [[ "$TAG_ONLY" == "1" && -z "$tag" ]]; then
  echo "ERROR: --tag-only requires --tag"
  exit 1
fi

# 默认推 master；仅 --tag-only 时跳过分支。
if [[ "$TAG_ONLY" == "1" ]]; then
  PUSH_BRANCHES=0
else
  PUSH_BRANCHES=1
fi

echo "==> FORCE=$FORCE TAG=$TAG TAG_ONLY=$TAG_ONLY → push_branches=$PUSH_BRANCHES (master only)"

PUSH_FLAGS=()
if [[ "$FORCE" == "1" ]]; then
  PUSH_FLAGS+=(--force-with-lease)
fi

if [[ "$PUSH_BRANCHES" == "1" ]]; then
  for remote in "${REMOTES[@]}"; do
    for branch in "${BRANCHES[@]}"; do
      echo "==> git push ${PUSH_FLAGS[*]:-} $remote $branch"
      git push "${PUSH_FLAGS[@]}" "$remote" "$branch"
    done
  done
fi

if [[ -n "$tag" ]]; then
  if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    old="$(git rev-list -n 1 "$tag" | cut -c1-7)"
    echo "==> git tag -f $tag (retarget $old → HEAD)"
    git tag -f "$tag"
  else
    echo "==> git tag $tag"
    git tag "$tag"
  fi
  for remote in "${REMOTES[@]}"; do
    echo "==> git push $remote :refs/tags/$tag (delete remote tag if any)"
    git push "$remote" ":refs/tags/$tag" || true
    echo "==> git push $remote $tag"
    git push "$remote" "$tag"
  done
elif [[ "$PUSH_BRANCHES" != "1" ]]; then
  echo "ERROR: nothing to push; pass --tag, or omit --tag-only to push master"
  exit 1
fi

echo "==> done"
