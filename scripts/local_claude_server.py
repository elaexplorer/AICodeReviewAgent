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
  1. Clones / updates the waimea ADO repo locally into .repo_cache/
  2. Checks out the PR source branch (so CLAUDE.md reflects the PR state)
  3. Invokes the installed /code-review Claude plugin from that directory
     — this runs the full multi-agent flow: haiku gating, CLAUDE.md compliance
       via Sonnet×2, bug detection via Opus×2, plus validation agents
     — the plugin adapts to ADO: gh CLI calls fail gracefully and Claude
       falls back to the globally configured ADO MCP tools
     — invoked WITHOUT --comment so step 7 stops before posting any threads
     — only read-only ADO MCP tools are listed in --allowedTools as a second
       safety layer to prevent accidental comment creation
  4. Parses the plugin's terminal output into structured JSON via a fast
     secondary Haiku call
  5. Returns the JSON comment list to the cloud agent

The cloud agent then posts the union of high/critical comments from both
GPT-4 (container) and Claude (this server) to the ADO PR.

Usage:
  pip install flask requests
  python scripts/local_claude_server.py              # port 5010

Expose via tunnel so the cloud agent can reach it:
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
# Claude /code-review plugin invocation
# ---------------------------------------------------------------------------

# Appended to the /code-review prompt so Claude emits a parseable JSON array
# at the end of its step-7 terminal output instead of prose only.
_JSON_SUFFIX = (
    "\n\nAfter completing your review findings (step 7), append a raw JSON array "
    "at the very end of your response — no prose after it. "
    "Each item must have exactly these fields:\n"
    '{"filePath":"/path/to/file","startLine":1,"endLine":1,'
    '"severity":"critical|high|medium|low","commentType":"issue",'
    '"commentText":"description","suggestedFix":"fix or empty","confidence":0.9}\n'
    "Return [] if no issues were found."
)


def run_plugin_review(pr_link: str, repo_dir: Path, pat: str = "") -> list[dict]:
    """
    Invoke the /code-review Claude plugin from the waimea repo checkout.

    No --comment flag: Claude runs the full multi-agent flow (haiku gating,
    CLAUDE.md scan via Sonnet×2, bug detection via Opus×2, validation agents)
    and outputs findings to stdout without posting to ADO.

    --dangerouslySkipPermissions gives Claude unrestricted access to all MCP
    tools (ADO read/search) so it can fetch the PR diff and CLAUDE.md.

    The JSON array Claude appends to its step-7 output is extracted and
    returned to the container, which posts using its pipeline PAT.

    Raises RuntimeError on timeout or non-zero exit.
    """
    print(f"[Review] Invoking /code-review plugin for {pr_link}", flush=True)

    env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
    env["PYTHONUTF8"] = "1"

    plugin_prompt = f"/code-review {pr_link}{_JSON_SUFFIX}"

    with _claude_semaphore:
        try:
            result = subprocess.run(
                [
                    "claude", "--print",
                    "--dangerouslySkipPermissions",
                    "--model", CLAUDE_MODEL,
                ],
                input=plugin_prompt,
                capture_output=True,
                text=True,
                encoding="utf-8",
                timeout=1200,   # 20 min for the full multi-agent flow
                env=env,
                cwd=str(repo_dir),
            )
        except subprocess.TimeoutExpired:
            raise RuntimeError("Claude /code-review plugin timed out after 20 min")

    print(f"[Review] Plugin exit: {result.returncode}", flush=True)
    if result.returncode != 0:
        snippet = (result.stderr or "").strip()[:400]
        raise RuntimeError(
            f"Claude plugin failed (exit {result.returncode}): {snippet or '(no output)'}"
        )

    output = result.stdout.strip()
    print(f"[Review] Plugin output ({len(output)} chars):\n{output[:600]}", flush=True)

    if not output:
        raise RuntimeError("Claude plugin produced no output — review did not complete")

    comments = _parse_json_from_output(output)
    print(f"[Review] {len(comments)} comment(s) collected", flush=True)
    return comments


def _parse_json_from_output(text: str) -> list[dict]:
    """
    Extract the last JSON array from Claude's plugin output.
    Searches from the end to find the outermost [...] block.
    """
    last_close = text.rfind("]")
    if last_close == -1:
        return []

    # Walk backwards to find the matching opening bracket
    depth = 0
    start = -1
    for i in range(last_close, -1, -1):
        if text[i] == "]":
            depth += 1
        elif text[i] == "[":
            depth -= 1
            if depth == 0:
                start = i
                break

    if start == -1:
        return []

    try:
        items = json.loads(text[start:last_close + 1])
        if not isinstance(items, list):
            return []
        normalised = []
        for item in items:
            if not isinstance(item, dict):
                continue
            comment_text = (
                item.get("commentText") or item.get("comment_text") or item.get("comment", "")
            )
            if not comment_text:
                continue
            normalised.append({
                "filePath":    item.get("filePath") or item.get("file_path", ""),
                "startLine":   int(item.get("startLine") or item.get("start_line") or 1),
                "endLine":     int(item.get("endLine")   or item.get("end_line")   or 1),
                "severity":    item.get("severity",    "medium"),
                "commentType": item.get("commentType") or item.get("comment_type") or "issue",
                "commentText": comment_text,
                "suggestedFix":item.get("suggestedFix") or item.get("suggested_fix") or "",
                "confidence":  float(item.get("confidence", 0.9)),
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
    """
    Background thread:
      1. Clone/update the waimea repo and check out the PR source branch.
      2. Invoke the /code-review plugin (no --comment; Claude does not post).
      3. Return the collected comments — the container posts using its pipeline PAT.
    """
    try:
        pr = get_pr_metadata(ado_pat, pr_id)
        repo_dir = ensure_repo_cloned(ado_pat)
        checkout_pr_branch(repo_dir, pr)

        comments = run_plugin_review(pr_link, repo_dir=repo_dir)
        print(f"[Job {job_id[:8]}] PR #{pr_id} → {len(comments)} comment(s)", flush=True)

        with _jobs_lock:
            _jobs[job_id] = {
                "status":        "done",
                "pullRequestId": pr_id,
                "comments":      comments,
            }
    except Exception as e:
        print(f"[Job {job_id[:8]}] Error: {e}", flush=True)
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
