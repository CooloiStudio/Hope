#!/usr/bin/env bash
# Hope — 双远端一键推送（Git Bash，无需 make）
#
# 用法：
#   ./scripts/push-remotes.sh
#   ./scripts/push-remotes.sh --force
#   ./scripts/push-remotes.sh --tag v0.13.90
#   ./scripts/push-remotes.sh --force --tag 0.13.90
#   ./scripts/push-remotes.sh --tag-only --tag v0.13.90

set -euo pipefail

REMOTES=(origin gitee)
BRANCHES=(release master develop)
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
echo "==> FORCE=$FORCE TAG=$TAG TAG_ONLY=$TAG_ONLY"

PUSH_FLAGS=()
TAG_FLAGS=()
if [[ "$FORCE" == "1" ]]; then
  PUSH_FLAGS+=(--force-with-lease)
  TAG_FLAGS+=(-f)
fi

if [[ "$TAG_ONLY" != "1" ]]; then
  for remote in "${REMOTES[@]}"; do
    for branch in "${BRANCHES[@]}"; do
      echo "==> git push ${PUSH_FLAGS[*]:-} $remote $branch"
      git push "${PUSH_FLAGS[@]}" "$remote" "$branch"
    done
  done
fi

tag="$(normalize_tag "$TAG")"
if [[ "$TAG_ONLY" == "1" && -z "$tag" ]]; then
  echo "ERROR: --tag-only requires --tag"
  exit 1
fi

if [[ -n "$tag" ]]; then
  echo "==> git tag ${TAG_FLAGS[*]:-} $tag"
  git tag "${TAG_FLAGS[@]}" "$tag"
  for remote in "${REMOTES[@]}"; do
    # tag 强制推送用 --force：--force-with-lease 对 tag 常因缺少远端跟踪而报 stale info
    if [[ "$FORCE" == "1" ]]; then
      echo "==> git push --force $remote $tag"
      git push --force "$remote" "$tag"
    else
      echo "==> git push $remote $tag"
      git push "$remote" "$tag"
    fi
  done
fi

echo "==> done"
