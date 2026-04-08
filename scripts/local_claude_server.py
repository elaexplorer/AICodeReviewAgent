#!/usr/bin/env python3
# -*- coding: utf-8 -*-
import sys, io, os
os.environ.setdefault("PYTHONUTF8", "1")
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding="utf-8", errors="replace")
"""
local_claude_server.py

Exposes a local HTTP endpoint that the cloud code review agent calls to get
Claude's review comments for a PR.

When called, it:
  1. Ensures the ADO repo is cloned/updated locally (for CLAUDE.md context)
  2. Checks out the PR source branch
  3. Invokes the code-review:code-review Claude skill with 5 parallel agents
     (CLAUDE.md compliance, bugs, git history, previous PRs, code comments)
  4. Instructs the skill to return JSON instead of posting to ADO
  5. Returns the JSON comment list to the cloud agent

The cloud agent intersects these with its GPT-4/5 comments and only posts
comments flagged by BOTH models.

Usage:
  pip install flask
  python scripts/local_claude_server.py              # port 5010

Expose via tunnel so the cloud agent can reach it:
  ngrok http 5010
  devtunnel host -p 5010 --allow-anonymous

Set on the container app:
  az containerapp update --name code-review-agent \\
    --resource-group rg-code-review-agent \\
    --set-env-vars "LOCAL_CLAUDE_AGENT_URL=https://<tunnel-url>"
"""

import argparse
import json
import os
import subprocess
import textwrap
import threading
import uuid
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

REPO_CACHE_DIR = Path(os.environ.get(
    "REPO_CACHE_DIR",
    str(REPO_ROOT / ".repo_cache" / ADO_REPOSITORY)
))

import requests as _requests
import base64


# ---------------------------------------------------------------------------
# ADO helpers (for repo clone auth + PR metadata)
# ---------------------------------------------------------------------------

def ado_headers(pat: str) -> dict:
    token = base64.b64encode(f":{pat}".encode()).decode()
    return {"Authorization": f"Basic {token}", "Content-Type": "application/json"}


def get_pr_metadata(pat: str, pr_id: int) -> dict:
    url = (f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/"
           f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}?api-version=7.0")
    r = _requests.get(url, headers=ado_headers(pat), timeout=30)
    r.raise_for_status()
    return r.json()


def get_pr_changed_files(pat: str, pr_id: int) -> list[dict]:
    """Return list of changed files with their paths and change types."""
    url = (f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/"
           f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations?api-version=7.0")
    r = _requests.get(url, headers=ado_headers(pat), timeout=30)
    r.raise_for_status()
    iterations = r.json().get("value", [])
    if not iterations:
        return []
    latest_iter = iterations[-1]["id"]

    url2 = (f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/"
            f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}"
            f"/iterations/{latest_iter}/changes?api-version=7.0")
    r2 = _requests.get(url2, headers=ado_headers(pat), timeout=30)
    r2.raise_for_status()
    changes = r2.json().get("changeEntries", [])
    return [
        {
            "path":       c["item"]["path"],
            "changeType": c.get("changeType", "edit"),
        }
        for c in changes
        if "path" in c.get("item", {})
    ]


def get_file_content_at_commit(pat: str, path: str, commit_id: str) -> str:
    """Fetch raw file content at a specific commit. Returns empty string on error."""
    url = (f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/"
           f"git/repositories/{ADO_REPOSITORY}/items"
           f"?path={path}&versionDescriptor.version={commit_id}&versionDescriptor.versionType=commit"
           f"&$format=text&api-version=7.0")
    try:
        r = _requests.get(url, headers=ado_headers(pat), timeout=20)
        if r.status_code == 200:
            return r.text
    except Exception:
        pass
    return ""


def build_pr_context(pat: str, pr: dict, changed_files: list[dict], max_files: int = 30) -> str:
    """
    Build a text block with PR metadata and the content of each changed file
    at the source commit. Files are included in full up to MAX_FILE_BYTES;
    total content is capped at MAX_TOTAL_BYTES so the prompt stays tractable.
    Skipped/deleted files are listed but content is not fetched.
    """
    source_commit = pr.get("lastMergeSourceCommit", {}).get("commitId", "")
    title         = pr.get("title", "")
    description   = (pr.get("description") or "")[:800]

    lines = [
        f"## Pull Request: {title}",
        f"Description: {description}",
        f"Changed files ({len(changed_files)} total):",
    ]
    for f in changed_files:
        lines.append(f"  [{f['changeType']}] {f['path']}")
    lines.append("")

    MAX_FILE_BYTES  = 6_000   # ~1500 tokens per file
    MAX_TOTAL_BYTES = 40_000  # ~10K tokens total — keeps Claude well within timeout

    shown = 0
    total_bytes = len("\n".join(lines))

    # Only include real source code files, skip lock/yaml/config/docs
    code_exts = {".rs", ".cs", ".py", ".go", ".ts", ".js", ".cpp", ".c", ".h", ".md"}
    skip_names = {"Cargo.lock", "package-lock.json", "yarn.lock"}

    eligible = [
        f for f in changed_files
        if f.get("path")
        and f["changeType"] != "delete"
        and any(f["path"].endswith(e) for e in code_exts)
        and not any(f["path"].endswith(s) for s in skip_names)
    ]

    # Added files first (new code most likely to have bugs), then edits
    sorted_files = sorted(eligible, key=lambda f: (0 if f["changeType"] == "add" else 1, f["path"]))

    for f in sorted_files[:max_files]:
        path   = f["path"]
        change = f["changeType"]

        content = get_file_content_at_commit(pat, path, source_commit) if source_commit else ""
        if not content:
            continue

        content = content[:MAX_FILE_BYTES]
        block = f"\n### {path} [{change}]\n```\n{content}\n```\n"
        total_bytes += len(block)
        if total_bytes > MAX_TOTAL_BYTES:
            lines.append(f"\n[... content truncated at {shown} files, {total_bytes // 1024} KB total ...]")
            break
        lines.append(block)
        shown += 1

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Repo clone / update
# ---------------------------------------------------------------------------

def ensure_repo_cloned(pat: str) -> Path:
    """Clone the ADO repo locally if not present, otherwise fetch latest."""
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
        subprocess.run(
            ["git", "remote", "set-url", "origin", clone_url],
            cwd=str(REPO_CACHE_DIR), capture_output=True, timeout=15
        )
        subprocess.run(
            ["git", "fetch", "origin", "--depth=50", "--prune"],
            cwd=str(REPO_CACHE_DIR), capture_output=True, timeout=120
        )
    return REPO_CACHE_DIR


def checkout_pr_branch(repo_dir: Path, pr: dict):
    """Check out the PR source branch so CLAUDE.md reflects the PR state."""
    source_commit = pr.get("lastMergeSourceCommit", {}).get("commitId", "")
    source_ref    = pr.get("sourceRefName", "").replace("refs/heads/", "")

    if source_commit:
        r = subprocess.run(
            ["git", "checkout", "--detach", source_commit],
            cwd=str(repo_dir), capture_output=True, text=True, timeout=30
        )
        if r.returncode == 0:
            print(f"[Repo] Checked out commit {source_commit[:8]}")
            return

    if source_ref:
        subprocess.run(
            ["git", "fetch", "origin", source_ref, "--depth=10"],
            cwd=str(repo_dir), capture_output=True, timeout=60
        )
        subprocess.run(
            ["git", "checkout", "-B", source_ref, f"origin/{source_ref}"],
            cwd=str(repo_dir), capture_output=True, timeout=30
        )
        print(f"[Repo] Checked out branch {source_ref}")


# ---------------------------------------------------------------------------
# Claude review invocation
# ---------------------------------------------------------------------------

# Prompt template — PR diff context is injected by Python before calling Claude.
# No MCP needed: diffs are fetched by the server and passed directly.
REVIEW_PROMPT = textwrap.dedent("""
    You are a senior code reviewer. Review the following pull request diff and return a JSON array of issues.

    {pr_context}

    Focus on:
    - Runtime panics, crashes, or process-terminating assertions on reachable paths
    - Data races, concurrency bugs
    - Security vulnerabilities (injection, auth bypass, credential exposure)
    - Breaking API / ABI changes visible to external callers
    - Correctness bugs: wrong logic, missing error handling on failure paths
    - Incomplete migrations where one file was changed but a dependent file was not

    Scoring rules:
    - Only include issues with confidence >= 0.85
    - "critical": will cause a production outage or security breach
    - "high": likely causes incorrect behaviour or crashes under realistic conditions
    - "medium": meaningful code quality issue but not immediately dangerous
    - "low": minor style or nitpick

    Return ONLY a raw JSON array — no prose, no markdown fences, nothing else.
    Each element must have exactly these fields:
    {{
      "filePath":    "/path/to/file",
      "startLine":   42,
      "endLine":     42,
      "severity":    "critical" | "high" | "medium" | "low",
      "commentType": "issue" | "suggestion",
      "commentText": "Clear, specific description referencing the actual code",
      "suggestedFix": "Concrete fix, or empty string",
      "confidence":  0.0-1.0
    }}

    Return [] if no significant issues survive the confidence filter.
""").strip()


def run_skill_review(pr_link: str, repo_dir: Path, pat: str = "", pr: dict | None = None) -> list[dict]:
    """
    Fetch PR diffs via ADO REST (no MCP), build context, and send to Claude.
    Claude runs as a stateless subprocess with the diff embedded in the prompt.
    """
    print(f"[Review] Starting Claude review for {pr_link}", flush=True)

    # Fetch diff context from ADO
    try:
        pr_id = int(pr_link.rstrip("/").split("/")[-1].split("?")[0])
        if not pat:
            pat = os.environ.get("ADO_PAT", "")
        if not pr:
            pr = get_pr_metadata(pat, pr_id)
        changed_files = get_pr_changed_files(pat, pr_id)
        print(f"[Review] {len(changed_files)} changed files", flush=True)
        pr_context = build_pr_context(pat, pr, changed_files)
    except Exception as e:
        print(f"[Review] Failed to build PR context: {e}", flush=True)
        return []

    prompt = REVIEW_PROMPT.format(pr_context=pr_context)
    print(f"[Review] Prompt size: {len(prompt)} chars", flush=True)

    env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
    env["PYTHONUTF8"] = "1"
    try:
        # Run from a temp dir so Claude doesn't load .mcp.json from the project
        # (MCP server startup adds 2+ minutes of overhead we don't need here —
        #  all ADO data is already embedded in the prompt)
        # Semaphore ensures only one claude --print process runs at a time;
        # concurrent invocations caused the second process to return nothing.
        import tempfile
        with _claude_semaphore, tempfile.TemporaryDirectory() as tmpdir:
            result = subprocess.run(
                ["claude", "--print", "--output-format", "json",
                 "--model", CLAUDE_MODEL],
                input=prompt,
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=420,      # 7 min — prompt is ~40KB, Claude takes 2-4 min for a full review
                env=env,
                cwd=tmpdir,
            )

        print(f"[Review] Claude exit code: {result.returncode}", flush=True)
        if result.returncode != 0:
            print(f"[Review] stderr: {result.stderr[:400]}", flush=True)
            return []

        stdout = "\n".join(
            l for l in result.stdout.splitlines()
            if not l.startswith("process.env[")
        ).strip()

        try:
            outer = json.loads(stdout)
            text  = outer.get("result", stdout)
        except json.JSONDecodeError:
            text = stdout

        comments = parse_json_comments(text)
        print(f"[Review] {len(comments)} comments parsed", flush=True)
        if not comments:
            print(f"[Review] Raw result (first 600): {text[:600]}", flush=True)
        return comments

    except subprocess.TimeoutExpired:
        print("[Review] Claude timed out after 7 minutes", flush=True)
        return []
    except Exception as e:
        print(f"[Review] Failed: {e}", flush=True)
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
# Async job store  (in-memory — survives the request, cleared on restart)
# ---------------------------------------------------------------------------

# job_id -> {"status": "pending"|"done"|"error", "pullRequestId": int, "comments": [...], "error": str}
_jobs: dict[str, dict] = {}
_jobs_lock = threading.Lock()
_claude_semaphore = threading.Semaphore(1)  # only one claude --print at a time


def _run_review_job(job_id: str, pr_link: str, ado_pat: str,
                    pr_id: int, project: str, repo: str):
    """Background thread: fetch PR diffs and run Claude review."""
    try:
        pr = get_pr_metadata(ado_pat, pr_id)
        comments = run_skill_review(pr_link, repo_dir=None, pat=ado_pat, pr=pr)
        print(f"[Job {job_id[:8]}] PR #{pr_id} -> {len(comments)} comments", flush=True)

        with _jobs_lock:
            _jobs[job_id] = {
                "status":        "done",
                "pullRequestId": pr_id,
                "comments":      comments,
            }
    except Exception as e:
        print(f"[Job {job_id[:8]}] Error: {e}")
        with _jobs_lock:
            _jobs[job_id] = {
                "status": "error",
                "error":  str(e),
            }


# ---------------------------------------------------------------------------
# Flask app
# ---------------------------------------------------------------------------

app = Flask(__name__)


@app.get("/health")
def health():
    return jsonify({
        "status":     "ok",
        "model":      CLAUDE_MODEL,
        "repoCache":  str(REPO_CACHE_DIR),
        "repoCached": REPO_CACHE_DIR.exists(),
    })


@app.post("/claudeCodeReview")
def review():
    """
    Submit a review job.  Returns immediately with {"jobId": "..."} and
    starts the Claude skill in a background thread.
    Poll GET /claudeCodeReview/<jobId> for the result.
    """
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
    job_id = str(uuid.uuid4())
    print(f"\n[Review] PR #{pr_id} ({project}/{repo}) -> job {job_id[:8]}")

    with _jobs_lock:
        _jobs[job_id] = {"status": "pending", "pullRequestId": pr_id}

    t = threading.Thread(
        target=_run_review_job,
        args=(job_id, pr_link, ado_pat, pr_id, project, repo),
        daemon=True,
    )
    t.start()

    return jsonify({"jobId": job_id, "pullRequestId": pr_id, "status": "pending"})


@app.get("/claudeCodeReview/<job_id>")
def review_result(job_id: str):
    """Poll for job result.  Returns 202 while pending, 200 when done."""
    with _jobs_lock:
        job = _jobs.get(job_id)

    if job is None:
        return jsonify({"error": "Job not found"}), 404

    if job["status"] == "pending":
        return jsonify({"jobId": job_id, "status": "pending"}), 202

    if job["status"] == "error":
        return jsonify({"jobId": job_id, "status": "error", "error": job.get("error")}), 500

    # done
    return jsonify({
        "jobId":         job_id,
        "status":        "done",
        "pullRequestId": job.get("pullRequestId"),
        "comments":      job.get("comments", []),
    })


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Local Claude skill review server")
    parser.add_argument("--port", type=int, default=5010)
    args = parser.parse_args()

    print(f"Local Claude review server")
    print(f"  Skill:    code-review:code-review")
    print(f"  Model:    {CLAUDE_MODEL}")
    print(f"  Repo:     {ADO_ORGANIZATION}/{ADO_PROJECT}/{ADO_REPOSITORY}")
    print(f"  Cache:    {REPO_CACHE_DIR}")
    print(f"  Port:     {args.port}")
    print()

    app.run(host="0.0.0.0", port=args.port)
