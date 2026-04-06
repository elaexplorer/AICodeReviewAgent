#!/usr/bin/env python3
"""
local_claude_server.py

Exposes a local HTTP endpoint so the cloud code review agent can call
Claude reviews in parallel alongside its own GPT-4 review.

Claude runs INSIDE a local clone of the repository with full tool access
(Read, Glob, Grep) so it can explore callers, tests, and related files —
not just the raw diff.

Usage:
  pip install flask
  python scripts/local_claude_server.py              # port 5010
  python scripts/local_claude_server.py --port 5010

Expose via tunnel so the cloud agent can reach it:
  ngrok http 5010
  devtunnel host -p 5010 --allow-anonymous

Then set on the container:
  az containerapp update --name code-review-agent \
    --resource-group rg-code-review-agent \
    --set-env-vars "LOCAL_CLAUDE_AGENT_URL=https://<tunnel-url>"
"""

import argparse
import base64
import json
import os
import subprocess
import sys
import textwrap
from pathlib import Path

from flask import Flask, jsonify, request

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
ENV_FILE  = REPO_ROOT / ".env"


def load_env():
    if not ENV_FILE.exists():
        return
    for line in ENV_FILE.read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, val = line.partition("=")
        os.environ.setdefault(key.strip(), val.strip())


load_env()

ADO_ORGANIZATION = os.environ.get("ADO_ORGANIZATION", "Skype")
ADO_PROJECT      = os.environ.get("ADO_PROJECT", "SCC")
ADO_REPOSITORY   = os.environ.get("ADO_REPOSITORY", "service-shared_framework_waimea")
CLAUDE_MODEL     = os.environ.get("CLAUDE_REVIEW_MODEL", "claude-sonnet-4-6")

# Where to cache the repo clone — defaults to .repo_cache/<repo> under the project root
REPO_CACHE_DIR = Path(os.environ.get(
    "REPO_CACHE_DIR",
    str(REPO_ROOT / ".repo_cache" / ADO_REPOSITORY)
))

import requests as _requests

# ---------------------------------------------------------------------------
# ADO helpers
# ---------------------------------------------------------------------------

def ado_headers(pat: str) -> dict:
    token = base64.b64encode(f":{pat}".encode()).decode()
    return {"Authorization": f"Basic {token}", "Content-Type": "application/json"}


def ado_get(pat: str, path: str, **kwargs) -> dict:
    url = f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/{path}"
    r = _requests.get(url, headers=ado_headers(pat), params=kwargs, timeout=30)
    r.raise_for_status()
    return r.json()


def get_pr_metadata(pat: str, pr_id: int) -> dict:
    return ado_get(pat, f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}",
                   **{"api-version": "7.0"})


def get_changed_file_paths(pat: str, pr_id: int) -> list[str]:
    """Return list of changed file paths in the PR."""
    iters = ado_get(pat,
                    f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations",
                    **{"api-version": "7.0"})
    latest = iters["value"][-1]["id"]
    changes = ado_get(pat,
                      f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations/{latest}/changes",
                      **{"api-version": "7.0", "$top": "100"})
    return [
        entry.get("item", {}).get("path", "")
        for entry in changes.get("changeEntries", [])
        if entry.get("item", {}).get("path")
    ]


def get_pr_diff_text(pat: str, pr_id: int) -> str:
    """Fetch a unified diff summary of changed files."""
    iters = ado_get(pat,
                    f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations",
                    **{"api-version": "7.0"})
    latest_id = iters["value"][-1]["id"]

    changes = ado_get(pat,
                      f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations/{latest_id}/changes",
                      **{"api-version": "7.0", "$top": "100"})

    pr      = get_pr_metadata(pat, pr_id)
    source_commit = pr.get("lastMergeSourceCommit", {}).get("commitId", "")
    source_branch = pr.get("sourceRefName", "").replace("refs/heads/", "")

    lines = []
    for entry in changes.get("changeEntries", []):
        path        = entry.get("item", {}).get("path", "")
        change_type = entry.get("changeType", "edit")
        lines.append(f"\n### {path}  (changeType={change_type})")
        if change_type == "delete":
            lines.append("[file deleted]")
            continue
        try:
            params = {"path": path, "api-version": "7.0", "$format": "text"}
            if source_commit:
                params["versionDescriptor.versionType"] = "commit"
                params["versionDescriptor.version"]     = source_commit
            else:
                params["versionDescriptor.versionType"] = "branch"
                params["versionDescriptor.version"]     = source_branch
            r = _requests.get(
                f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/"
                f"git/repositories/{ADO_REPOSITORY}/items",
                headers=ado_headers(pat), params=params, timeout=30)
            lines.append(f"```\n{r.text[:5000]}\n```" if r.status_code == 200 else f"[HTTP {r.status_code}]")
        except Exception as e:
            lines.append(f"[error: {e}]")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Repo clone / update
# ---------------------------------------------------------------------------

def ensure_repo_cloned(pat: str) -> Path:
    """
    Clone the ADO repo into REPO_CACHE_DIR if not present.
    If already cloned, fetch latest so branches are up to date.
    Returns the local repo path.
    """
    clone_url = (
        f"https://x:{pat}@dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}"
        f"/_git/{ADO_REPOSITORY}"
    )

    if not REPO_CACHE_DIR.exists():
        print(f"[Repo] Cloning {ADO_REPOSITORY} into {REPO_CACHE_DIR} ...")
        REPO_CACHE_DIR.parent.mkdir(parents=True, exist_ok=True)
        result = subprocess.run(
            ["git", "clone", "--depth=100", clone_url, str(REPO_CACHE_DIR)],
            capture_output=True, text=True, timeout=300
        )
        if result.returncode != 0:
            raise RuntimeError(f"git clone failed: {result.stderr[:400]}")
        print("[Repo] Clone complete")
    else:
        print(f"[Repo] Fetching latest into {REPO_CACHE_DIR} ...")
        # Re-set remote URL in case PAT changed
        subprocess.run(
            ["git", "remote", "set-url", "origin", clone_url],
            cwd=str(REPO_CACHE_DIR), capture_output=True, timeout=15
        )
        subprocess.run(
            ["git", "fetch", "origin", "--depth=50", "--prune"],
            cwd=str(REPO_CACHE_DIR), capture_output=True, text=True, timeout=120
        )

    return REPO_CACHE_DIR


def checkout_pr_branch(repo_dir: Path, pr: dict):
    """
    Fetch and check out the PR source branch so Claude sees the exact
    state of the code being reviewed.
    """
    source_ref    = pr.get("sourceRefName", "").replace("refs/heads/", "")
    source_commit = pr.get("lastMergeSourceCommit", {}).get("commitId", "")

    if source_commit:
        # Detach HEAD at the exact PR tip commit
        result = subprocess.run(
            ["git", "checkout", "--detach", source_commit],
            cwd=str(repo_dir), capture_output=True, text=True, timeout=30
        )
        if result.returncode == 0:
            print(f"[Repo] Checked out commit {source_commit[:8]}")
            return

    if source_ref:
        subprocess.run(
            ["git", "fetch", "origin", source_ref, "--depth=10"],
            cwd=str(repo_dir), capture_output=True, timeout=60
        )
        subprocess.run(
            ["git", "checkout", "-B", source_ref, f"origin/{source_ref}"],
            cwd=str(repo_dir), capture_output=True, text=True, timeout=30
        )
        print(f"[Repo] Checked out branch {source_ref}")


# ---------------------------------------------------------------------------
# Claude review — full codebase context
# ---------------------------------------------------------------------------

REVIEW_PROMPT = textwrap.dedent("""
    You are a senior code reviewer with full access to this repository.

    PR Title:      {title}
    Source Branch: {source}
    Target Branch: {target}
    PR Author:     {author}

    Changed files in this PR:
    {changed_files}

    PR content (changed file contents at PR head):
    {diff}

    You have Read, Glob, and Grep tools available to explore the full repository.
    Before writing comments, use your tools to:
    1. Read the complete changed files for full context beyond the diff
    2. Find and read callers or consumers of any changed functions / classes / interfaces
    3. Check related tests to understand expected behaviour
    4. Grep for similar patterns elsewhere to judge consistency
    5. Read any interfaces or base classes that changed code implements

    This context will help you catch real issues (broken callers, violated contracts,
    missing test coverage) rather than surface-level observations.

    After exploring the codebase, return ONLY a raw JSON array — no prose, no markdown
    fences. Each element must have exactly these fields:
    {{
      "filePath":    "/path/to/file",
      "startLine":   42,
      "endLine":     42,
      "severity":    "critical" | "medium" | "low",
      "commentType": "issue" | "suggestion" | "nitpick",
      "commentText": "Clear explanation, referencing what you found in the codebase",
      "suggestedFix": "Optional concrete fix",
      "confidence":  0.0-1.0
    }}

    Focus on:
    - Bugs and logic errors
    - Security vulnerabilities
    - Breaking changes to callers
    - Missing error handling
    - Inconsistency with patterns in the rest of the codebase

    Only include comments with confidence >= 0.7.
    Return [] if no issues found.
""").strip()


def run_claude_review(pr: dict, diff: str, changed_files: list[str], repo_dir: Path) -> list[dict]:
    """
    Run Claude inside the local repo clone so it has full tool access
    (Read, Glob, Grep) to explore the codebase before commenting.
    """
    changed_files_str = "\n".join(f"  - {f}" for f in changed_files) or "  (none)"
    prompt = REVIEW_PROMPT.format(
        title=pr.get("title", ""),
        source=pr.get("sourceRefName", ""),
        target=pr.get("targetRefName", ""),
        author=pr.get("createdBy", {}).get("displayName", ""),
        changed_files=changed_files_str,
        diff=diff[:20000],
    )

    print(f"[Claude] Running review in {repo_dir} with full repo context...")
    try:
        env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
        result = subprocess.run(
            [
                "claude", "--print",
                "--output-format", "json",
                "--model", CLAUDE_MODEL,
                "--allowedTools", "Read,Glob,Grep",
            ],
            input=prompt,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=300,
            env=env,
            cwd=str(repo_dir),   # <-- run inside the repo
        )
        if result.returncode != 0:
            print(f"[Claude] Error exit {result.returncode}: {result.stderr[:300]}")
            return []

        stdout = "\n".join(
            l for l in result.stdout.splitlines()
            if not l.startswith("process.env[")
        ).strip()

        outer = json.loads(stdout)
        text  = outer.get("result", stdout)
        comments = parse_json_comments(text)
        print(f"[Claude] {len(comments)} comments generated")
        return comments

    except subprocess.TimeoutExpired:
        print("[Claude] Timed out after 5 minutes")
        return []
    except Exception as e:
        print(f"[Claude] Failed: {e}")
        return []


def parse_json_comments(text: str) -> list[dict]:
    text  = text.strip()
    start = text.find("[")
    end   = text.rfind("]")
    if start == -1 or end == -1:
        return []
    try:
        items = json.loads(text[start:end + 1])
        if not isinstance(items, list):
            return []
        normalised = []
        for item in items:
            if not isinstance(item, dict):
                continue
            normalised.append({
                "filePath":    item.get("filePath")    or item.get("file_path", ""),
                "startLine":   int(item.get("startLine") or item.get("start_line") or item.get("lineNumber") or 1),
                "endLine":     int(item.get("endLine")   or item.get("end_line")   or item.get("lineNumber") or 1),
                "severity":    item.get("severity",    "medium"),
                "commentType": item.get("commentType") or item.get("comment_type") or "issue",
                "commentText": item.get("commentText") or item.get("comment_text") or item.get("comment", ""),
                "suggestedFix":item.get("suggestedFix") or item.get("suggested_fix") or "",
                "confidence":  float(item.get("confidence", 0.8)),
            })
        return normalised
    except (json.JSONDecodeError, ValueError):
        return []


def parse_pr_link(pr_link: str) -> tuple[int, str, str] | None:
    import re
    m = re.search(r"pullrequest/(\d+)", pr_link, re.IGNORECASE)
    if not m:
        return None
    pr_id = int(m.group(1))
    parts = pr_link.replace("https://", "").split("/")
    try:
        git_idx = parts.index("_git")
        project = parts[git_idx - 1]
        repo    = parts[git_idx + 1].split("?")[0].split("/")[0]
    except (ValueError, IndexError):
        project = ADO_PROJECT
        repo    = ADO_REPOSITORY
    return pr_id, project, repo


# ---------------------------------------------------------------------------
# Flask app
# ---------------------------------------------------------------------------

app = Flask(__name__)


@app.get("/health")
def health():
    return jsonify({
        "status":   "ok",
        "model":    CLAUDE_MODEL,
        "repoDir":  str(REPO_CACHE_DIR),
        "repoCached": REPO_CACHE_DIR.exists(),
    })


@app.post("/review")
def review():
    data    = request.get_json(force=True) or {}
    pr_link = data.get("pullRequestLink", "")

    ado_pat = (
        request.headers.get("X-Ado-Access-Token")
        or os.environ.get("ADO_PAT", "")
    )

    if not pr_link:
        return jsonify({"error": "pullRequestLink is required"}), 400
    if not ado_pat:
        return jsonify({"error": "ADO_PAT not configured"}), 500

    parsed = parse_pr_link(pr_link)
    if not parsed:
        return jsonify({"error": f"Could not parse PR ID from: {pr_link}"}), 400

    pr_id, project, repo = parsed
    print(f"\n[Review] PR #{pr_id} ({project}/{repo})")

    try:
        pr            = get_pr_metadata(ado_pat, pr_id)
        changed_files = get_changed_file_paths(ado_pat, pr_id)
        diff          = get_pr_diff_text(ado_pat, pr_id)

        print(f"[Review] {len(changed_files)} changed files: {', '.join(changed_files[:5])}")

        # Ensure repo is cloned locally and checked out at PR head
        repo_dir = ensure_repo_cloned(ado_pat)
        checkout_pr_branch(repo_dir, pr)

        comments = run_claude_review(pr, diff, changed_files, repo_dir)
        print(f"[Review] PR #{pr_id} → {len(comments)} comments")

        return jsonify({"pullRequestId": pr_id, "comments": comments})

    except Exception as e:
        print(f"[Review] Error: {e}")
        return jsonify({"error": str(e)}), 500


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Local Claude review server — full repo context")
    parser.add_argument("--port", type=int, default=5010)
    args = parser.parse_args()

    print(f"Local Claude review server")
    print(f"  Model:    {CLAUDE_MODEL}")
    print(f"  Repo:     {ADO_ORGANIZATION}/{ADO_PROJECT}/{ADO_REPOSITORY}")
    print(f"  Cache:    {REPO_CACHE_DIR}")
    print(f"  Port:     {args.port}")
    print()

    app.run(host="0.0.0.0", port=args.port)
