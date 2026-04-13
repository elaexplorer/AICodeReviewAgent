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



# ---------------------------------------------------------------------------
# Claude /code-review plugin invocation
# ---------------------------------------------------------------------------

# Appended after the skill invocation so Claude emits a parseable JSON array
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


def run_plugin_review(pr_link: str, repo_dir: Path) -> list[dict]:
    """
    Invoke the code-review:code-review skill via claude --print.

    Uses Popen to stream Claude's output to the server log in real time
    (so we can see sub-agent progress) while also accumulating it for
    JSON parsing at the end.

    Raises RuntimeError on timeout or non-zero exit.
    """
    print(f"[Review] Invoking /code-review:code-review skill for {pr_link}", flush=True)

    plugin_prompt = f"/code-review:code-review {pr_link}{_JSON_SUFFIX}"

    env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
    env["PYTHONUTF8"] = "1"

    def _drain(stream, lines: list, tag: str):
        """Read stream line by line, print each line, and accumulate."""
        for raw in stream:
            line = raw.rstrip("\n")
            print(f"[Claude{tag}] {line}", flush=True)
            lines.append(line)

    with _claude_semaphore:
        proc = subprocess.Popen(
            [
                "claude", "--print",
                "--dangerously-skip-permissions",
                "--model", CLAUDE_MODEL,
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

        stdout_lines: list[str] = []
        stderr_lines: list[str] = []

        t_out = threading.Thread(target=_drain, args=(proc.stdout, stdout_lines, ""), daemon=True)
        t_err = threading.Thread(target=_drain, args=(proc.stderr, stderr_lines, " ERR"), daemon=True)
        t_out.start()
        t_err.start()

        try:
            proc.stdin.write(plugin_prompt)
            proc.stdin.close()
            proc.wait(timeout=2400)   # 40 min for the full multi-agent flow
        except subprocess.TimeoutExpired:
            proc.kill()
            t_out.join(timeout=5)
            t_err.join(timeout=5)
            raise RuntimeError("Claude /code-review plugin timed out after 40 min")

        t_out.join()
        t_err.join()

    returncode = proc.returncode
    print(f"[Review] Plugin exit: {returncode}", flush=True)
    if returncode != 0:
        snippet = "\n".join(stderr_lines[-20:])[:400]
        raise RuntimeError(
            f"Claude plugin failed (exit {returncode}): {snippet or '(no output)'}"
        )

    output = "\n".join(stdout_lines).strip()
    print(f"[Review] Plugin output ({len(output)} chars) — last 600 chars:\n{output[-600:]}", flush=True)

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


def _run_review_job(job_id: str, pr_link: str, pr_id: int, project: str, repo: str):
    """
    Background thread: invoke the /code-review:code-review skill and return
    the collected comments. The skill fetches everything it needs via ADO MCP
    tools — no local repo clone or PAT required here.
    """
    try:
        # Use the cached repo dir as cwd if it exists (Claude can read local
        # CLAUDE.md), otherwise fall back to the agent root directory.
        cwd = REPO_CACHE_DIR if REPO_CACHE_DIR.exists() else REPO_ROOT

        comments = run_plugin_review(pr_link, repo_dir=cwd)
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

    if not pr_link:
        return jsonify({"error": "pullRequestLink is required"}), 400

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
