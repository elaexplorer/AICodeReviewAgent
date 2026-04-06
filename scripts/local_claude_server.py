#!/usr/bin/env python3
"""
local_claude_server.py

Exposes a local HTTP endpoint so the cloud code review agent can call
Claude reviews in parallel alongside its own GPT-4 review.

Usage:
  python scripts/local_claude_server.py              # port 5010
  python scripts/local_claude_server.py --port 5020

Then expose via tunnel so the cloud agent can reach it:
  ngrok http 5010
  # or: devtunnel host -p 5010

Set LOCAL_CLAUDE_AGENT_URL on the container app to the tunnel URL.
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


def get_pr_diff(pat: str, pr_id: int) -> str:
    pr = ado_get(pat, f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}",
                 **{"api-version": "7.0"})
    source_commit = pr.get("lastMergeSourceCommit", {}).get("commitId", "")
    source_branch = pr.get("sourceRefName", "").replace("refs/heads/", "")

    iters = ado_get(pat, f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations",
                    **{"api-version": "7.0"})
    latest = iters["value"][-1]["id"]

    changes = ado_get(pat,
                      f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations/{latest}/changes",
                      **{"api-version": "7.0", "$top": "100"})

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
# Claude review
# ---------------------------------------------------------------------------

REVIEW_PROMPT = textwrap.dedent("""
    You are a senior code reviewer. Review the following pull request diff and
    return ONLY a JSON array of review comments. No prose, no markdown fences —
    raw JSON array only.

    PR Title: {title}
    Source Branch: {source}
    Target Branch: {target}

    Changed files and content:
    {diff}

    Return a JSON array where each element has exactly these fields:
    {{
      "filePath":    "/path/to/file",
      "startLine":   42,
      "endLine":     42,
      "severity":    "critical" | "medium" | "low",
      "commentType": "issue" | "suggestion" | "nitpick",
      "commentText": "Clear explanation of the problem",
      "suggestedFix": "Optional concrete fix",
      "confidence":  0.0-1.0
    }}

    Focus on:
    - Bugs and logic errors
    - Security vulnerabilities
    - Missing error handling or validation
    - Cross-file consistency issues

    Only include comments with confidence >= 0.7.
    Return [] if no issues found.
""").strip()


def run_claude_review(pr: dict, diff: str) -> list[dict]:
    prompt = REVIEW_PROMPT.format(
        title=pr.get("title", ""),
        source=pr.get("sourceRefName", ""),
        target=pr.get("targetRefName", ""),
        diff=diff[:20000],
    )
    try:
        env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
        result = subprocess.run(
            ["claude", "--print", "--output-format", "json", "--model", CLAUDE_MODEL],
            input=prompt, capture_output=True, text=True,
            encoding="utf-8", timeout=300, env=env,
        )
        if result.returncode != 0:
            print(f"[Claude] Error exit {result.returncode}: {result.stderr[:200]}")
            return []

        stdout = "\n".join(
            l for l in result.stdout.splitlines()
            if not l.startswith("process.env[")
        ).strip()
        outer = json.loads(stdout)
        text  = outer.get("result", stdout)
        return parse_json_comments(text)
    except subprocess.TimeoutExpired:
        print("[Claude] Timed out")
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
                "commentType": item.get("commentType") or item.get("comment_type") or item.get("type", "issue"),
                "commentText": item.get("commentText") or item.get("comment_text") or item.get("comment", ""),
                "suggestedFix":item.get("suggestedFix") or item.get("suggested_fix") or "",
                "confidence":  float(item.get("confidence", 0.8)),
            })
        return normalised
    except (json.JSONDecodeError, ValueError):
        return []


def parse_pr_link(pr_link: str) -> tuple[int, str, str] | None:
    """Return (pr_id, project, repository) from a PR URL, or None."""
    import re
    m = re.search(r"pullrequest/(\d+)", pr_link, re.IGNORECASE)
    if not m:
        return None
    pr_id = int(m.group(1))
    # project / repo from URL segments
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
    return jsonify({"status": "ok", "model": CLAUDE_MODEL})


@app.post("/review")
def review():
    data   = request.get_json(force=True) or {}
    pr_link = data.get("pullRequestLink", "")

    # Allow caller to pass their ADO token; fall back to .env ADO_PAT
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
    print(f"[Review] PR #{pr_id} ({project}/{repo})")

    try:
        pr   = ado_get(ado_pat, f"git/repositories/{repo}/pullRequests/{pr_id}",
                       **{"api-version": "7.0"})
        diff = get_pr_diff(ado_pat, pr_id)
        comments = run_claude_review(pr, diff)
        print(f"[Review] PR #{pr_id} → {len(comments)} comments")
        return jsonify({"pullRequestId": pr_id, "comments": comments})
    except Exception as e:
        print(f"[Review] Error: {e}")
        return jsonify({"error": str(e)}), 500


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Local Claude review server")
    parser.add_argument("--port", type=int, default=5010)
    args = parser.parse_args()
    print(f"Starting local Claude review server on port {args.port}")
    print(f"Model: {CLAUDE_MODEL}")
    app.run(host="0.0.0.0", port=args.port)
