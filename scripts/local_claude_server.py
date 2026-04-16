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
import logging
import os
import subprocess
import threading
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

from flask import Flask, jsonify, request

# ---------------------------------------------------------------------------
# Logging — write to both console AND a log file with timestamps so execution
# timing is always visible regardless of how the server is started.
# ---------------------------------------------------------------------------

_LOG_FILE = Path(__file__).resolve().parent.parent / "local_claude_server.log"

def _setup_logging():
    fmt = logging.Formatter("%(asctime)s %(message)s", datefmt="%H:%M:%S")
    root = logging.getLogger()
    root.setLevel(logging.DEBUG)

    # Console handler
    ch = logging.StreamHandler(sys.stdout)
    ch.setFormatter(fmt)
    root.addHandler(ch)

    # File handler — always appends, so restarts don't wipe history
    fh = logging.FileHandler(str(_LOG_FILE), encoding="utf-8")
    fh.setFormatter(fmt)
    root.addHandler(fh)

_setup_logging()
_log = logging.getLogger(__name__)

def _ts() -> str:
    """UTC timestamp string for inline timing annotations."""
    return datetime.now(timezone.utc).strftime("%H:%M:%S")

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

# How many Claude subprocesses may run in parallel.
# Each run consumes ~500 MB RAM and fires Haiku+Sonnet×2+Opus×2 API calls.
# Set MAX_CONCURRENT_REVIEWS=1 in .env to serialise (safe but slow).
# Default 2 works on any machine with 8 GB+ RAM without hitting rate limits.
MAX_CONCURRENT_REVIEWS = int(os.environ.get("MAX_CONCURRENT_REVIEWS", "2"))

REPO_CACHE_DIR = Path(os.environ.get(
    "REPO_CACHE_DIR",
    str(REPO_ROOT / ".repo_cache" / ADO_REPOSITORY)
))



# ---------------------------------------------------------------------------
# Claude direct-prompt review (no slash command — works under any OS user)
# ---------------------------------------------------------------------------

def _build_review_prompt(pr_link: str) -> str:
    """
    Build a self-contained review prompt for Claude.
    Uses ADO REST API via curl ($ADO_PAT env var) to fetch PR data.
    Returns findings as a JSON array — no plugin or command discovery needed.
    """
    return f"""You are performing a code review for an Azure DevOps pull request.

PR URL: {pr_link}

URL format: https://{{org}}.visualstudio.com/{{project}}/_git/{{repo}}/pullrequest/{{prId}}
Parse the URL to extract ORG, PROJECT, REPO, PR_ID.

CONSTRAINTS (strictly enforced):
- Do NOT post any comment, thread, vote, or review to the PR.
- Do NOT call any ADO MCP write tools.
- Your response MUST end with a raw JSON array and NOTHING after it.

STEPS:

1. Get PR details and changed files using curl with basic auth (password = $ADO_PAT):
   curl -s -u ":$ADO_PAT" "https://ORG.visualstudio.com/PROJECT/_apis/git/repositories/REPO/pullRequests/PR_ID?api-version=7.0"
   curl -s -u ":$ADO_PAT" "https://ORG.visualstudio.com/PROJECT/_apis/git/repositories/REPO/pullRequests/PR_ID/iterations?api-version=7.0"
   curl -s -u ":$ADO_PAT" "https://ORG.visualstudio.com/PROJECT/_apis/git/repositories/REPO/pullRequests/PR_ID/iterations/LAST_ITERATION_ID/changes?api-version=7.0"

2. For files with changeType add/edit/rename (skip deleted, test files, generated files, binaries):
   Fetch content at the source commit:
   curl -s -u ":$ADO_PAT" "https://ORG.visualstudio.com/PROJECT/_apis/git/repositories/REPO/items?path=FILE_PATH&version=SOURCE_COMMIT_SHA&versionType=commit&api-version=7.0"
   Limit to 25 files, prioritising .cs .py .ts .js .go .java .rs files.

3. Review changed code for:
   - Bugs: null dereferences, off-by-one errors, broken error handling, race conditions, resource leaks
   - Security: hardcoded secrets, missing auth checks, injection vulnerabilities
   - Logic errors: wrong conditions, missing edge cases
   - Performance: N+1 queries, blocking async calls, unnecessary allocations
   Skip: pre-existing issues, style/formatting, missing tests (unless a bug is introduced), false positives.

4. Output ONLY a raw JSON array as the very last thing — no prose after it:
[{{"filePath":"/path/to/file","startLine":1,"endLine":1,"severity":"critical|high|medium|low","commentType":"issue","commentText":"description","suggestedFix":"fix or empty string","confidence":0.85}}]
Return [] if no real issues found. Only include issues with confidence >= 0.60.
"""

# ADO MCP tools that write to a PR — blocked via --allowedTools so Claude
# cannot post even if it tries to ignore the prompt instruction above.
_ADO_WRITE_TOOLS = [
    "mcp__azure-devops__repo_create_pull_request",
    "mcp__azure-devops__repo_create_pull_request_thread",
    "mcp__azure-devops__repo_reply_to_comment",
    "mcp__azure-devops__repo_update_pull_request",
    "mcp__azure-devops__repo_update_pull_request_thread",
    "mcp__azure-devops__repo_update_pull_request_reviewers",
    "mcp__azure-devops__repo_vote_pull_request",
    "mcp__azure-devops__repo_create_branch",
    "mcp__azure-devops__wit_create_work_item",
    "mcp__azure-devops__wit_add_work_item_comment",
    "mcp__azure-devops__wit_update_work_item",
    "mcp__azure-devops__wiki_create_or_update_page",
]


def run_plugin_review(pr_link: str, repo_dir: Path) -> list[dict]:
    """
    Send a direct review prompt to Claude via claude --print.

    Uses Popen to stream Claude's output to the server log in real time
    (so we can see sub-agent progress) while also accumulating it for
    JSON parsing at the end.

    Always returns a list (may be empty) — never raises, so the caller
    always gets a consistent "done" response even if Claude fails or times out.
    """
    _log.info("[Review] ── START ── direct prompt review for %s", pr_link)
    t0 = time.monotonic()

    def _elapsed() -> str:
        secs = int(time.monotonic() - t0)
        return f"{secs // 60}m{secs % 60:02d}s"

    plugin_prompt = _build_review_prompt(pr_link)

    env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
    env["PYTHONUTF8"] = "1"

    def _drain(stream, lines: list, tag: str):
        """Read stream line by line, log each line, and accumulate."""
        for raw in stream:
            line = raw.rstrip("\n")
            _log.info("[Claude%s +%s] %s", tag, _elapsed(), line)
            lines.append(line)

    stdout_lines: list[str] = []
    stderr_lines: list[str] = []

    _log.info("[Review] Waiting for concurrency slot (max %d parallel runs)...", MAX_CONCURRENT_REVIEWS)
    with _claude_semaphore:
        semaphore_wait = time.monotonic() - t0
        _log.info("[Review] Semaphore acquired after %.1fs — launching subprocess", semaphore_wait)

        proc = subprocess.Popen(
            [
                "claude", "--print",
                "--dangerously-skip-permissions",
                "--model", CLAUDE_MODEL,
                "--disallowedTools", ",".join(_ADO_WRITE_TOOLS),
            ],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            cwd=str(repo_dir),
        )

        t_out = threading.Thread(target=_drain, args=(proc.stdout, stdout_lines, ""), daemon=True)
        t_err = threading.Thread(target=_drain, args=(proc.stderr, stderr_lines, " ERR"), daemon=True)
        t_out.start()
        t_err.start()

        timed_out = False
        try:
            proc.stdin.write(plugin_prompt)
            proc.stdin.close()
            proc.wait(timeout=2400)   # 40 min hard limit
        except subprocess.TimeoutExpired:
            proc.kill()
            timed_out = True
        finally:
            t_out.join(timeout=5)
            t_err.join(timeout=5)

    returncode = proc.returncode
    output = "\n".join(stdout_lines).strip()
    elapsed_total = time.monotonic() - t0
    elapsed_str = f"{int(elapsed_total) // 60}m{int(elapsed_total) % 60:02d}s"

    if timed_out:
        _log.warning("[Review] ── TIMEOUT ── after %s (40 min hard limit) — trying to recover partial output (%d lines)",
                     elapsed_str, len(stdout_lines))
        comments = _parse_json_from_output(output)
        _log.info("[Review] Recovered %d comment(s) from partial output", len(comments))
        return comments

    _log.info("[Review] Plugin exit=%d elapsed=%s output=%d chars", returncode, elapsed_str, len(output))
    if returncode != 0:
        comments = _parse_json_from_output(output)
        if comments:
            _log.info("[Review] Exit %d but recovered %d comment(s) from output", returncode, len(comments))
            return comments
        snippet = "\n".join(stderr_lines[-20:])[:400]
        _log.warning("[Review] ── FAILED ── exit=%d elapsed=%s stderr=%s",
                     returncode, elapsed_str, snippet or "(none)")
        return []

    if not output:
        _log.warning("[Review] ── NO OUTPUT ── Claude produced nothing after %s", elapsed_str)
        return []

    _log.info("[Review] Output: %d chars — last 400:\n%s", len(output), output[-400:])
    comments = _parse_json_from_output(output)
    _log.info("[Review] ── DONE ── %d comment(s) in %s", len(comments), elapsed_str)
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
# Semaphore caps parallel Claude subprocesses at MAX_CONCURRENT_REVIEWS.
# Jobs that arrive while all slots are busy queue in their background thread
# and start as soon as a slot frees up.
_claude_semaphore = threading.Semaphore(MAX_CONCURRENT_REVIEWS)


def _run_review_job(job_id: str, pr_link: str, pr_id: int, project: str, repo: str):
    """
    Background thread: invoke the /code-review:code-review skill and return
    the collected comments. The skill fetches everything it needs via ADO MCP
    tools — no local repo clone or PAT required here.

    Always sets job status to "done" (never "error") so the cloud agent gets
    a consistent response. Errors are logged server-side only.
    """
    short = job_id[:8]
    t0 = time.monotonic()
    _log.info("[Job %s] PR #%d (%s/%s) — starting", short, pr_id, project, repo)

    # Always use REPO_ROOT as cwd so Claude finds .claude/commands/code-review.md.
    # The review command uses ADO MCP tools and does not need the local repo clone.
    cwd = REPO_ROOT

    try:
        comments = run_plugin_review(pr_link, repo_dir=cwd)
    except Exception as e:
        # run_plugin_review should never raise (it returns [] on failure),
        # but guard here so a bug doesn't leave the job stuck as pending.
        _log.exception("[Job %s] Unexpected error: %s", short, e)
        comments = []

    elapsed = time.monotonic() - t0
    elapsed_str = f"{int(elapsed) // 60}m{int(elapsed) % 60:02d}s"
    _log.info("[Job %s] PR #%d → %d comment(s) in %s", short, pr_id, len(comments), elapsed_str)

    with _jobs_lock:
        _jobs[job_id] = {
            "status":        "done",
            "pullRequestId": pr_id,
            "comments":      comments,
            "elapsedSeconds": int(elapsed),
        }


# ---------------------------------------------------------------------------
# Flask app
# ---------------------------------------------------------------------------

app = Flask(__name__)


@app.get("/health")
def health():
    with _jobs_lock:
        pending  = sum(1 for j in _jobs.values() if j["status"] == "pending")
        done     = sum(1 for j in _jobs.values() if j["status"] == "done")
    # Semaphore._value is the number of FREE slots remaining
    active = MAX_CONCURRENT_REVIEWS - _claude_semaphore._value
    return jsonify({
        "status":             "ok",
        "model":              CLAUDE_MODEL,
        "repoCache":          str(REPO_CACHE_DIR),
        "repoCached":         REPO_CACHE_DIR.exists(),
        "maxConcurrent":      MAX_CONCURRENT_REVIEWS,
        "activeReviews":      active,
        "pendingJobs":        pending,
        "completedJobs":      done,
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

    if not pr_link:
        return jsonify({"error": "pullRequestLink is required"}), 400

    parsed = parse_pr_link(pr_link)
    if not parsed:
        return jsonify({"error": f"Could not parse PR ID from: {pr_link}"}), 400

    pr_id, project, repo = parsed
    job_id = str(uuid.uuid4())
    _log.info("[Review] PR #%d (%s/%s) → job %s submitted at %s", pr_id, project, repo, job_id[:8], _ts())

    with _jobs_lock:
        _jobs[job_id] = {"status": "pending", "pullRequestId": pr_id}

    t = threading.Thread(
        target=_run_review_job,
        args=(job_id, pr_link, pr_id, project, repo),
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
        "jobId":          job_id,
        "status":         "done",
        "pullRequestId":  job.get("pullRequestId"),
        "comments":       job.get("comments", []),
        "elapsedSeconds": job.get("elapsedSeconds"),
    })


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Local Claude skill review server")
    parser.add_argument("--port", type=int, default=5010)
    args = parser.parse_args()

    _log.info("Local Claude review server starting")
    _log.info("  Skill       : code-review:code-review")
    _log.info("  Model       : %s", CLAUDE_MODEL)
    _log.info("  Concurrency : %d parallel Claude runs (MAX_CONCURRENT_REVIEWS)", MAX_CONCURRENT_REVIEWS)
    _log.info("  Repo        : %s/%s/%s", ADO_ORGANIZATION, ADO_PROJECT, ADO_REPOSITORY)
    _log.info("  Cache       : %s", REPO_CACHE_DIR)
    _log.info("  Port        : %d", args.port)
    _log.info("  Log         : %s", _LOG_FILE)

    app.run(host="0.0.0.0", port=args.port)
