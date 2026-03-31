#!/usr/bin/env python3
"""
daily_pr_review.py

Runs daily (via Windows Task Scheduler at 10am) to review all PRs updated
in the last 24 hours in the service-shared_framework_waimea repo.

For each PR:
  1. Fetch the diff from ADO REST API
  2. Run local Claude (claude --print) for a review → JSON comments
  3. Call cloud agent review-by-link → gpt-5.4 comments → JSON comments
  4. Consolidate both via Claude meta-review (deduplicate, merge, rank)
  5. POST consolidated comments to cloud agent /comments/post (fingerprint dedup)

Usage:
  python daily_pr_review.py                     # reviews PRs updated in last 24h
  python daily_pr_review.py --pr 1379723        # review a specific PR
  python daily_pr_review.py --dry-run           # review + consolidate but don't post
  python daily_pr_review.py --hours 48          # look back 48 hours instead of 24
"""

import argparse
import base64
import json
import os
import subprocess
import sys
import textwrap
from datetime import datetime, timedelta, timezone
from pathlib import Path

import requests

# ---------------------------------------------------------------------------
# Config — loaded from .env in the repo root
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

ADO_PAT          = os.environ.get("ADO_PAT", "")
ADO_ORGANIZATION = os.environ.get("ADO_ORGANIZATION", "Skype")
ADO_PROJECT      = os.environ.get("ADO_PROJECT", "SCC")
ADO_REPOSITORY   = os.environ.get("ADO_REPOSITORY", "service-shared_framework_waimea")
CLOUD_AGENT_URL  = os.environ.get("CLOUD_AGENT_URL",
    "https://code-review-agent.icycliff-b5eb5e7d.eastus.azurecontainerapps.io")
CLAUDE_MODEL     = os.environ.get("CLAUDE_REVIEW_MODEL", "claude-sonnet-4-6")
LOOKBACK_HOURS   = int(os.environ.get("LOOKBACK_HOURS", "24"))

# ---------------------------------------------------------------------------
# ADO REST helpers
# ---------------------------------------------------------------------------

def ado_headers():
    token = base64.b64encode(f":{ADO_PAT}".encode()).decode()
    return {"Authorization": f"Basic {token}", "Content-Type": "application/json"}

def ado_get(path: str, **kwargs) -> dict:
    url = f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/{path}"
    r = requests.get(url, headers=ado_headers(), params=kwargs, timeout=30)
    r.raise_for_status()
    return r.json()

def get_updated_prs(since: datetime) -> list[dict]:
    """Return active PRs whose last update time is >= since."""
    data = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests",
        **{"searchCriteria.status": "active", "api-version": "7.0", "$top": "100"}
    )
    since_utc = since.astimezone(timezone.utc)
    prs = []
    for pr in data.get("value", []):
        updated = pr.get("creationDate") or ""
        # also check last merge source commit date if available
        try:
            updated_dt = datetime.fromisoformat(updated.replace("Z", "+00:00"))
        except ValueError:
            continue
        if updated_dt >= since_utc:
            prs.append(pr)
    return prs

def get_pr_by_id(pr_id: int) -> dict:
    data = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}",
        **{"api-version": "7.0"}
    )
    return data

def get_pr_diff(pr_id: int) -> str:
    """Fetch changed files with content at PR head (source branch)."""
    # Get PR metadata to extract the source branch
    pr = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}",
        **{"api-version": "7.0"}
    )
    source_commit = pr.get("lastMergeSourceCommit", {}).get("commitId", "")
    source_branch  = pr.get("sourceRefName", "").replace("refs/heads/", "")

    # Get latest iteration changes
    iters = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations",
        **{"api-version": "7.0"}
    )
    latest = iters["value"][-1]["id"]

    changes = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests/{pr_id}/iterations/{latest}/changes",
        **{"api-version": "7.0", "$top": "100"}
    )

    lines = []
    for entry in changes.get("changeEntries", []):
        path        = entry.get("item", {}).get("path", "")
        change_type = entry.get("changeType", "edit")
        lines.append(f"\n### {path}  (changeType={change_type})")

        if change_type == "delete":
            lines.append("[file deleted]")
            continue

        # Fetch file content at PR source commit (fall back to branch name)
        try:
            params = {"path": path, "api-version": "7.0", "$format": "text"}
            if source_commit:
                params["versionDescriptor.versionType"] = "commit"
                params["versionDescriptor.version"]     = source_commit
            else:
                params["versionDescriptor.versionType"] = "branch"
                params["versionDescriptor.version"]     = source_branch

            r = requests.get(
                f"https://dev.azure.com/{ADO_ORGANIZATION}/{ADO_PROJECT}/_apis/"
                f"git/repositories/{ADO_REPOSITORY}/items",
                headers=ado_headers(),
                params=params,
                timeout=30,
            )
            if r.status_code == 200:
                lines.append(f"```\n{r.text[:5000]}\n```")
            else:
                lines.append(f"[HTTP {r.status_code}]")
        except Exception as e:
            lines.append(f"[error: {e}]")

    return "\n".join(lines)

# ---------------------------------------------------------------------------
# Step 1: Claude local review
# ---------------------------------------------------------------------------

CLAUDE_REVIEW_PROMPT = textwrap.dedent("""
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
      "severity":    "high" | "medium" | "low",
      "commentType": "issue" | "suggestion" | "nitpick",
      "commentText": "Clear explanation of the problem",
      "suggestedFix": "Optional concrete fix",
      "confidence":  0.0-1.0
    }}

    Focus on:
    - Bugs and logic errors
    - Security vulnerabilities
    - Integration assumptions that may not hold (especially for new/added files)
    - Missing error handling or validation
    - Cross-file consistency issues

    Only include comments with confidence >= 0.7.
    Return [] if no issues found.
""").strip()

def run_claude_review(pr: dict, diff: str) -> list[dict]:
    """Invoke local Claude CLI and return parsed comment list."""
    prompt = CLAUDE_REVIEW_PROMPT.format(
        title=pr.get("title", ""),
        source=pr.get("sourceRefName", ""),
        target=pr.get("targetRefName", ""),
        diff=diff[:20000],  # keep within context limits
    )

    print("  [Claude] Running local review...")
    try:
        env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
        result = subprocess.run(
            ["claude", "--print", "--output-format", "json",
             "--model", CLAUDE_MODEL],
            input=prompt,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=300,
            env=env,
        )
        if result.returncode != 0:
            print(f"  [Claude] Error (exit {result.returncode}):")
            print(f"    stdout: {result.stdout[:300]}")
            print(f"    stderr: {result.stderr[:300]}")
            return []

        # Strip any warning lines before the JSON (e.g. ANTHROPIC_LOG warnings)
        stdout = "\n".join(
            line for line in result.stdout.splitlines()
            if not line.startswith("process.env[")
        ).strip()

        # claude --output-format json wraps result in {"result": "...", ...}
        outer = json.loads(stdout)
        text  = outer.get("result", stdout)
        return parse_json_comments(text)

    except subprocess.TimeoutExpired:
        print("  [Claude] Timed out after 5 minutes")
        return []
    except Exception as e:
        print(f"  [Claude] Failed: {e}")
        return []

# ---------------------------------------------------------------------------
# Step 2: Cloud agent review (review-by-link, no post)
# ---------------------------------------------------------------------------

def run_cloud_agent_review(pr_id: int) -> list[dict]:
    """Call cloud agent review-by-link and return comment list."""
    pr_link = (
        f"https://skype.visualstudio.com/{ADO_PROJECT}/_git/"
        f"{ADO_REPOSITORY}/pullrequest/{pr_id}"
    )
    print(f"  [Cloud agent] Calling review-by-link for PR {pr_id}...")
    try:
        r = requests.post(
            f"{CLOUD_AGENT_URL}/api/codereview/review-by-link",
            json={"pullRequestLink": pr_link},
            headers={"X-Ado-Access-Token": ADO_PAT},
            timeout=300,
        )
        r.raise_for_status()
        data = r.json()
        comments = data.get("comments", [])
        print(f"  [Cloud agent] Got {len(comments)} comments")
        return comments
    except Exception as e:
        print(f"  [Cloud agent] Failed: {e}")
        return []

# ---------------------------------------------------------------------------
# Step 3: Consolidation via Claude meta-review
# ---------------------------------------------------------------------------

CONSOLIDATION_PROMPT = textwrap.dedent("""
    You are a senior tech lead consolidating code review comments from two AI models
    (Model A = GPT-4, Model B = Claude) for the same pull request.

    Your job: produce the best possible final review comment list by:
    1. MERGING near-duplicate comments (same file + similar issue) → keep the
       clearer, more actionable wording; use the higher confidence score.
    2. BOOSTING confidence for comments raised by BOTH models (add 0.1, cap at 1.0).
    3. DROPPING comments where confidence < 0.65 AND only one model raised it.
    4. KEEPING all high-severity (high) comments regardless of confidence.
    5. PRESERVING unique medium/low comments from either model if confidence >= 0.7.

    Model A comments (GPT-4 / cloud agent):
    {cloud_comments}

    Model B comments (Claude / local):
    {claude_comments}

    Return ONLY a raw JSON array (no prose, no markdown fences) with the final
    consolidated comment list. Each element must have exactly these fields:
    {{
      "filePath":    "/path/to/file",
      "startLine":   42,
      "endLine":     42,
      "severity":    "high" | "medium" | "low",
      "commentType": "issue" | "suggestion" | "nitpick",
      "commentText": "Clear explanation",
      "suggestedFix": "",
      "confidence":  0.0-1.0
    }}

    Return [] if no comments survive consolidation.
""").strip()

def consolidate_comments(cloud_comments: list, claude_comments: list) -> list[dict]:
    """Use Claude to merge and deduplicate both model outputs."""
    if not cloud_comments and not claude_comments:
        return []
    if not cloud_comments:
        return claude_comments
    if not claude_comments:
        return cloud_comments

    prompt = CONSOLIDATION_PROMPT.format(
        cloud_comments=json.dumps(cloud_comments,  indent=2)[:8000],
        claude_comments=json.dumps(claude_comments, indent=2)[:8000],
    )

    print("  [Consolidation] Running meta-review...")
    try:
        env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_LOG"}
        result = subprocess.run(
            ["claude", "--print", "--output-format", "json",
             "--model", CLAUDE_MODEL],
            input=prompt,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=180,
            env=env,
        )
        if result.returncode != 0:
            print(f"  [Consolidation] Error (exit {result.returncode}), falling back to union")
            return cloud_comments + claude_comments

        stdout = "\n".join(
            line for line in result.stdout.splitlines()
            if not line.startswith("process.env[")
        ).strip()
        outer = json.loads(stdout)
        text  = outer.get("result", stdout)
        consolidated = parse_json_comments(text)
        print(f"  [Consolidation] {len(cloud_comments)} + {len(claude_comments)} -> {len(consolidated)} comments")
        return consolidated

    except Exception as e:
        print(f"  [Consolidation] Failed: {e}, falling back to union")
        return cloud_comments + claude_comments

# ---------------------------------------------------------------------------
# Step 4: Post via cloud agent /comments/post
# ---------------------------------------------------------------------------

def post_comments(pr_id: int, comments: list[dict], dry_run: bool) -> dict:
    """POST consolidated comments to the cloud agent endpoint."""
    if not comments:
        print("  [Post] No comments to post")
        return {"posted": 0, "skipped": 0, "failed": 0}

    if dry_run:
        print(f"  [Post] DRY RUN — would post {len(comments)} comments")
        for c in comments:
            print(f"    {c.get('severity','?').upper():6}  {c.get('filePath','')}:{c.get('startLine',1)}")
        return {"posted": 0, "skipped": 0, "failed": 0, "dry_run": True}

    payload = {
        "project":       ADO_PROJECT,
        "repository":    ADO_REPOSITORY,
        "pullRequestId": pr_id,
        "comments":      comments,
    }
    try:
        r = requests.post(
            f"{CLOUD_AGENT_URL}/api/codereview/comments/post",
            json=payload,
            headers={"X-Ado-Access-Token": ADO_PAT},
            timeout=120,
        )
        r.raise_for_status()
        result = r.json()
        print(f"  [Post] posted={result.get('posted',0)} skipped={result.get('skipped',0)} failed={result.get('failed',0)}")
        return result
    except Exception as e:
        print(f"  [Post] Failed: {e}")
        return {"posted": 0, "skipped": 0, "failed": len(comments)}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def parse_json_comments(text: str) -> list[dict]:
    """Extract a JSON array from model output, tolerating extra prose."""
    text = text.strip()
    # Find the outermost [ ... ]
    start = text.find("[")
    end   = text.rfind("]")
    if start == -1 or end == -1:
        return []
    try:
        items = json.loads(text[start:end + 1])
        if not isinstance(items, list):
            return []
        # Normalise field names (model may use camelCase or snake_case)
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
                "suggestedFix":item.get("suggestedFix") or item.get("suggested_fix") or item.get("suggestedfix", ""),
                "confidence":  float(item.get("confidence", 0.8)),
            })
        return normalised
    except (json.JSONDecodeError, ValueError):
        return []

def pr_link(pr_id: int) -> str:
    return (f"https://skype.visualstudio.com/{ADO_PROJECT}/_git/"
            f"{ADO_REPOSITORY}/pullrequest/{pr_id}")

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def review_pr(pr: dict, dry_run: bool):
    pr_id = pr["pullRequestId"]
    title = pr.get("title", "")
    print(f"\n{'='*60}")
    print(f"PR #{pr_id}: {title}")
    print(f"Link: {pr_link(pr_id)}")
    print(f"{'='*60}")

    # 1. Fetch diff
    print("  [Diff] Fetching changed files...")
    diff = get_pr_diff(pr_id)

    # 2. Claude local review + 3. Cloud agent review (in parallel via threads)
    from concurrent.futures import ThreadPoolExecutor, as_completed
    with ThreadPoolExecutor(max_workers=2) as executor:
        f_claude = executor.submit(run_claude_review, pr, diff)
        f_cloud  = executor.submit(run_cloud_agent_review, pr_id)
        claude_comments = f_claude.result()
        cloud_comments  = f_cloud.result()

    print(f"  [Results] Claude: {len(claude_comments)}  Cloud agent: {len(cloud_comments)}")

    # 4. Consolidate
    consolidated = consolidate_comments(cloud_comments, claude_comments)

    # 5. Post
    post_comments(pr_id, consolidated, dry_run)


def main():
    parser = argparse.ArgumentParser(description="Daily PR review — dual model + consolidate + post")
    parser.add_argument("--pr",      type=int, help="Review a specific PR by ID")
    parser.add_argument("--dry-run", action="store_true", help="Review and consolidate but don't post")
    parser.add_argument("--hours",   type=int, default=LOOKBACK_HOURS, help="Look-back window in hours (default 24)")
    args = parser.parse_args()

    if not ADO_PAT:
        print("ERROR: ADO_PAT is not set. Add it to .env or set as environment variable.")
        sys.exit(1)

    if args.pr:
        pr = get_pr_by_id(args.pr)
        review_pr(pr, args.dry_run)
    else:
        since = datetime.now(timezone.utc) - timedelta(hours=args.hours)
        print(f"Looking for PRs updated since {since.strftime('%Y-%m-%d %H:%M UTC')} "
              f"({args.hours}h lookback)")
        prs = get_updated_prs(since)
        if not prs:
            print("No PRs updated in the lookback window. Nothing to do.")
            return
        print(f"Found {len(prs)} PR(s) to review")
        for pr in prs:
            try:
                review_pr(pr, args.dry_run)
            except Exception as e:
                print(f"  ERROR reviewing PR #{pr.get('pullRequestId')}: {e}")

    print("\nDone.")


if __name__ == "__main__":
    main()
