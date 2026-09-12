#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把本目录（VisionForge 部署包）整份上传到 GitHub。

上传到：https://github.com/qian1586/-AI-SOP

为什么不用 git push
--------------------------------------------------------------------------
1. 目标机器上没有安装 Git for Windows（C:\\Program Files\\Git 不存在）；
2. 即使有，Codex 自带的那个 git 是精简运行时，缺少 git-remote-https，
   https 推送会报 `git: 'remote-https' is not a git command`。

所以这里绕开 git，直接用 GitHub 官方 REST API（Git Data API）：
   blob（每个文件）→ tree（整棵目录树）→ commit → 更新 main 分支
一次调用就是一个完整提交，效果等同一次 `git push`。

凭据从哪来（按顺序找）
--------------------------------------------------------------------------
1. 环境变量 GH_TOKEN / GITHUB_TOKEN（一键脚本会用 gh 登录后自动塞进来）
2. 本目录的 .github-token 文件（第一行；已在 .gitignore 里，不会被上传）

用法
--------------------------------------------------------------------------
    python upload-to-github.py                # 正常上传（版本号自动 +0.1）
    python upload-to-github.py --dry-run      # 只列文件，不写任何东西
    python upload-to-github.py --no-bump      # 不改版本号
    python upload-to-github.py --note "修了自学阈值"
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

# ---------------------------------------------------------------- 常量

OWNER = "qian1586"
REPO = "-AI-SOP"
BRANCH = "main"

# 本脚本在 <包根>\scripts\ 下，所以要往上一级
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOKEN_FILE = os.path.join(ROOT, ".github-token")

API = "https://api.github.com"
UA = "VisionForge-Uploader/1.1"


class Fail(Exception):
    """给现场同事看的、人话的失败。"""


# ---------------------------------------------------------------- HTTP

def _request(url: str, token: str | None, method: str = "GET", payload=None):
    data = None
    headers = {
        "User-Agent": UA,
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if token:
        headers["Authorization"] = "Bearer " + token
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            body = resp.read()
            return resp.status, (json.loads(body.decode("utf-8")) if body else None)
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        try:
            msg = json.loads(body).get("message", body)
        except Exception:
            msg = body
        return e.code, {"__error__": msg}
    except Exception as e:                       # 网络层
        raise Fail(f"连不上 GitHub：{e}")


def api(path: str, token: str, method: str = "GET", payload=None):
    status, body = _request(API + path, token, method, payload)
    if isinstance(body, dict) and "__error__" in body:
        msg = body["__error__"]
        if status == 401:
            raise Fail("凭据无效或已过期（401）。重新做一次登录即可。")
        if status == 403:
            raise Fail("GitHub 拒绝了这次操作（403）：通常是这个 token 没有该仓库的写权限，或触发了限流。\n原始信息：" + str(msg))
        if status == 404:
            raise Fail(f"接口不存在或无权访问（404）：{path}\n原始信息：{msg}")
        raise Fail(f"GitHub 返回 {status}：{msg}")
    return body


# ---------------------------------------------------------------- 文件清单

SKIP_DIRS = {".git", "bin", "obj", "data", "logs"}
SKIP_SUFFIX = (".user", ".suo", ".tmp", ".bak", ".log")
SKIP_NAMES = {"Thumbs.db", "Desktop.ini", ".DS_Store", ".github-token"}


def collect_files() -> list[str]:
    """
    列出"应该上传"的文件。优先问 git（.gitignore 里的规则它最清楚）；
    本机没有 git 就退回自己走目录 + 同一套排除规则 —— 结果一致。
    """
    try:
        out = subprocess.run(
            ["git", "-c", "safe.directory=*", "-c", "core.quotepath=false",
             "ls-files", "-co", "--exclude-standard"],
            cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if out.returncode == 0:
            files = sorted({ln.strip() for ln in out.stdout.splitlines() if ln.strip()})
            files = [f for f in files if os.path.basename(f) not in SKIP_NAMES]
            if files:
                return files
    except Exception:
        pass
    return _walk_fallback()


def _walk_fallback() -> list[str]:
    files = []
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            if name.endswith(SKIP_SUFFIX) or name in SKIP_NAMES:
                continue
            rel = os.path.relpath(os.path.join(dirpath, name), ROOT)
            files.append(rel.replace("\\", "/"))
    return sorted(files)


def blob_sha(data: bytes) -> str:
    """本地算 git blob 的 sha1 —— 用来判断"这个文件在远端是不是已经一样了"。"""
    h = hashlib.sha1()
    h.update(b"blob %d\0" % len(data))
    h.update(data)
    return h.hexdigest()


# ---------------------------------------------------------------- 版本号

def read_version() -> float:
    try:
        with open(os.path.join(ROOT, ".version"), "r", encoding="utf-8") as f:
            return float(f.read().strip())
    except Exception:
        return 1.0


def write_version(v: float) -> None:
    with open(os.path.join(ROOT, ".version"), "w", encoding="utf-8", newline="") as f:
        f.write(f"{v:.1f}")


# ---------------------------------------------------------------- 凭据

def read_token() -> str:
    for env in ("GH_TOKEN", "GITHUB_TOKEN"):
        v = os.environ.get(env, "").strip()
        if v:
            return v

    if os.path.exists(TOKEN_FILE):
        with open(TOKEN_FILE, "r", encoding="utf-8-sig") as f:
            for line in f:
                v = line.strip()
                if v:
                    return v

    raise Fail(
        "找不到 GitHub 凭据。\n"
        "· 正常做法：双击「上传到GitHub.cmd」，它会先带你登录 GitHub，再自动上传；\n"
        f"· 手工做法：把 GitHub 令牌存到 {TOKEN_FILE}（第一行），再运行本脚本。"
    )


# ---------------------------------------------------------------- 主流程

def remote_tree_map(token: str, commit_sha: str) -> dict[str, str]:
    """远端某个提交里 path → blob sha 的映射，用来少传没变化的文件。"""
    commit = api(f"/repos/{OWNER}/{REPO}/git/commits/{commit_sha}", token)
    tree = api(f"/repos/{OWNER}/{REPO}/git/trees/{commit['tree']['sha']}?recursive=1", token)
    return {e["path"]: e["sha"] for e in tree.get("tree", []) if e["type"] == "blob"}


def bootstrap_empty_repo(token: str) -> None:
    """
    GitHub 的空仓库不允许直接用 Git Data API 建 blob（会回 409 "Git Repository is empty."）。
    先用 Contents API 落一个文件，把仓库"点亮"，之后 blob/tree/commit 就都能用了。

    这里优先用本地的 README.md 当第一份文件（反正下一步整棵树会把它替换成最新版），
    没有就写一行占位文字。
    """
    path = os.path.join(ROOT, "README.md")
    if os.path.exists(path):
        with open(path, "rb") as f:
            content = base64.b64encode(f.read()).decode("ascii")
    else:
        content = base64.b64encode("VisionForge AI-SOP".encode("utf-8")).decode("ascii")

    api(f"/repos/{OWNER}/{REPO}/contents/README.md", token, "PUT", {
        "message": "初始化仓库（VisionForge AI-SOP）",
        "content": content,
    })


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true", help="只列文件，不写任何东西")
    ap.add_argument("--no-bump", action="store_true", help="不改版本号")
    ap.add_argument("--note", default="", help="这次改了什么")
    args = ap.parse_args()

    print("=" * 64)
    print(f" 上传目标：https://github.com/{OWNER}/{REPO}")
    print(f" 本地目录：{ROOT}")
    print("=" * 64)

    if not os.path.isdir(ROOT):
        raise Fail(f"目录不存在：{ROOT}")

    files = collect_files()
    print(f" 待上传文件：{len(files)} 个")

    if args.dry_run:
        for f in files:
            print("   -", f)
        print("\n（dry-run 结束，没有写任何东西）")
        return 0

    token = read_token()
    me = api("/user", token)
    print(f" 已登录：{me.get('login')}")

    repo = api(f"/repos/{OWNER}/{REPO}", token)
    perms = repo.get("permissions") or {}
    if "push" in perms and not perms["push"]:
        raise Fail(
            f"当前账号 {me.get('login')} 对这个仓库没有写权限。\n"
            f"请用 {OWNER} 登录，或把 {me.get('login')} 加成协作者（Write）。"
        )

    # 版本号 +0.1（V1.0 → V1.1 …），提交信息里带上，一眼看出这是第几次改
    current = read_version()
    if args.no_bump:
        tag = f"V{current:.1f}"
    else:
        next_version = round(current + 0.1, 1)
        tag = f"V{next_version:.1f}"
        write_version(next_version)
    print(f" 版本号：{tag}（上一版 V{current:.1f}）")

    message = (f"{tag} {args.note}" if args.note
               else f"{tag} 上传：追觅洗地机AI-SOP监测系统（源码 + 部署脚本 + 文档）")

    # 远端当前状态（新仓库是空的：404 / 409）
    parent_sha = None
    base_map: dict[str, str] = {}
    status, body = _request(f"{API}/repos/{OWNER}/{REPO}/git/ref/heads/{BRANCH}", token)
    if status == 200 and isinstance(body, dict) and "object" in body:
        parent_sha = body["object"]["sha"]
        print(f" 远端已有 {BRANCH} 分支：{parent_sha[:8]}")
        base_map = remote_tree_map(token, parent_sha)
    else:
        print(f" 远端 {BRANCH} 分支还是空的 —— 先落一个初始提交把仓库点亮")

        # 空仓库里 Git Data API 不能建 blob（409 Git Repository is empty），
        # 必须先用 Contents API 写一个文件造出第一个提交。
        bootstrap_empty_repo(token)

        status, body = _request(f"{API}/repos/{OWNER}/{REPO}/git/ref/heads/{BRANCH}", token)
        if status == 200 and isinstance(body, dict) and "object" in body:
            parent_sha = body["object"]["sha"]
            print(f" 初始提交已创建：{parent_sha[:8]}")
        else:
            raise Fail(f"初始化仓库失败（{status}）：{body}")

    entries = []
    created = reused = 0
    for i, rel in enumerate(files, 1):
        with open(os.path.join(ROOT, rel.replace("/", os.sep)), "rb") as f:
            data = f.read()

        local_sha = blob_sha(data)
        if base_map.get(rel) == local_sha:
            sha = local_sha
            reused += 1
        else:
            res = api(f"/repos/{OWNER}/{REPO}/git/blobs", token, "POST", {
                "content": base64.b64encode(data).decode("ascii"),
                "encoding": "base64",
            })
            sha = res["sha"]
            created += 1

        entries.append({"path": rel, "mode": "100644", "type": "blob", "sha": sha})
        if i % 25 == 0 or i == len(files):
            print(f"   ... {i}/{len(files)}")

    print(f" 文件准备完成：新上传 {created} 个，内容没变的复用 {reused} 个")

    # 刻意不传 base_tree：远端就是本地目录的镜像，本地删掉的文件远端也会消失
    tree = api(f"/repos/{OWNER}/{REPO}/git/trees", token, "POST", {"tree": entries})
    print(f" 目录树已创建：{tree['sha'][:8]}")

    payload = {"message": message, "tree": tree["sha"]}
    if parent_sha:
        payload["parents"] = [parent_sha]
    commit = api(f"/repos/{OWNER}/{REPO}/git/commits", token, "POST", payload)
    print(f" 提交已创建：{commit['sha'][:8]}  \"{message}\"")

    if parent_sha:
        api(f"/repos/{OWNER}/{REPO}/git/refs/heads/{BRANCH}", token, "PATCH",
            {"sha": commit["sha"], "force": False})
    else:
        api(f"/repos/{OWNER}/{REPO}/git/refs", token, "POST",
            {"ref": f"refs/heads/{BRANCH}", "sha": commit["sha"]})
    print(f" {BRANCH} 分支已更新")

    print("\n 上传完成 ✔")
    print(f" 打开看看：https://github.com/{OWNER}/{REPO}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fail as e:
        print("\n[失败] " + str(e), file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        sys.exit(130)
