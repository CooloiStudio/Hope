# Hope — 双远端推送（可选 make；无 make 时用下方脚本）
#
# PowerShell（推荐，Windows 自带/常用）：
#   pwsh ./scripts/push-remotes.ps1
#   pwsh ./scripts/push-remotes.ps1 -Force -Tag v0.13.90
#
# Git Bash（无需 make）：
#   ./scripts/push-remotes.sh
#   ./scripts/push-remotes.sh --force --tag v0.13.90
#
# Make（需自行安装 make）：
#   make push                         # 推送 release/master/develop → origin + gitee
#   make push FORCE=1                 # 强制覆盖远端（--force-with-lease）
#   make push TAG=v0.13.90            # 推送分支并打/推送 release tag
#   make push FORCE=1 TAG=0.13.90     # 强制推送 + tag（无 v 前缀会自动补上）
#
# 参数：
#   FORCE  0=普通推送（默认）  1=--force-with-lease
#   TAG    发版标签版本号，如 v0.13.90 或 0.13.90；留空则不处理 tag

SHELL := /bin/bash
.SHELLFLAGS := -eu -o pipefail -c

REMOTES  := origin gitee
BRANCHES := release master develop
GITEE_URL := git@gitee.com:CooloiStudio/Hope.git

FORCE ?= 0
TAG   ?=

ifeq ($(FORCE),1)
PUSH_FLAGS := --force-with-lease
TAG_FORCE  := -f
else
PUSH_FLAGS :=
TAG_FORCE  :=
endif

.PHONY: help remotes ensure-gitee push tag status

help:
	@echo "make push [FORCE=0|1] [TAG=vX.Y.Z]"
	@echo "  FORCE=1  使用 --force-with-lease 覆盖远端分支/标签"
	@echo "  TAG=...  创建并推送发版标签（可写 0.13.90，自动补 v）"
	@echo "远端: $(REMOTES)    分支: $(BRANCHES)"

ensure-gitee:
	@if ! git remote get-url gitee >/dev/null 2>&1; then \
		echo "==> add remote gitee $(GITEE_URL)"; \
		git remote add gitee "$(GITEE_URL)"; \
	fi

remotes: ensure-gitee
	@git remote -v

# 推送到两个远端的三条分支；若指定 TAG 则一并打标签并推送。
push: ensure-gitee
	@echo "==> FORCE=$(FORCE) TAG=$(TAG)"
	@for remote in $(REMOTES); do \
		for branch in $(BRANCHES); do \
			echo "==> git push $(PUSH_FLAGS) $$remote $$branch"; \
			git push $(PUSH_FLAGS) $$remote $$branch; \
		done; \
	done
	@if [[ -n "$(TAG)" ]]; then \
		$(MAKE) tag TAG="$(TAG)" FORCE="$(FORCE)"; \
	fi
	@echo "==> done"

# 仅打 tag 并推送到两个远端（不推分支）。
# 强制时 tag 使用 --force（lease 对 tag 易 stale info）；分支仍见 push 目标里的 FORCE。
tag: ensure-gitee
	@raw="$(TAG)"; \
	if [[ -z "$$raw" ]]; then echo "ERROR: TAG is required (e.g. TAG=v0.13.90)"; exit 1; fi; \
	if [[ "$$raw" == v* ]]; then tag="$$raw"; else tag="v$$raw"; fi; \
	echo "==> tag $$tag (FORCE=$(FORCE))"; \
	git tag $(TAG_FORCE) "$$tag"; \
	for remote in $(REMOTES); do \
		if [[ "$(FORCE)" == "1" ]]; then \
			echo "==> git push --force $$remote $$tag"; \
			git push --force $$remote "$$tag"; \
		else \
			echo "==> git push $$remote $$tag"; \
			git push $$remote "$$tag"; \
		fi; \
	done

status:
	@git status -sb
	@echo "---"
	@git remote -v
	@echo "---"
	@git branch -vv
