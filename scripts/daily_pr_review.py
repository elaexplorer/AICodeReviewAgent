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
import smtplib
import subprocess
import sys
import textwrap
from datetime import datetime, timedelta, timezone
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
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
ADO_BOT_PAT      = os.environ.get("ADO_BOT_PAT", "")   # if set, used for posting so comments appear under a bot identity
ADO_ORGANIZATION = os.environ.get("ADO_ORGANIZATION", "Skype")
ADO_PROJECT      = os.environ.get("ADO_PROJECT", "SCC")
ADO_REPOSITORY   = os.environ.get("ADO_REPOSITORY", "service-shared_framework_waimea")
CLOUD_AGENT_URL  = os.environ.get("CLOUD_AGENT_URL",
    "https://code-review-agent.icycliff-b5eb5e7d.eastus.azurecontainerapps.io")
CLAUDE_MODEL     = os.environ.get("CLAUDE_REVIEW_MODEL", "claude-sonnet-4-6")
LOOKBACK_HOURS   = int(os.environ.get("LOOKBACK_HOURS", "24"))

# Email config (optional — set in .env to enable)
EMAIL_FROM       = os.environ.get("REPORT_EMAIL_FROM", "")
EMAIL_TO         = os.environ.get("REPORT_EMAIL_TO", "")        # comma-separated
EMAIL_SMTP_HOST  = os.environ.get("REPORT_SMTP_HOST", "smtp.office365.com")
EMAIL_SMTP_PORT  = int(os.environ.get("REPORT_SMTP_PORT", "587"))
EMAIL_SMTP_USER  = os.environ.get("REPORT_SMTP_USER", "")
EMAIL_SMTP_PASS  = os.environ.get("REPORT_SMTP_PASS", "")
REPORTS_DIR      = REPO_ROOT / "reports"

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

def get_all_active_prs() -> list[dict]:
    """Return all active non-draft PRs regardless of age."""
    data = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests",
        **{"searchCriteria.status": "active", "searchCriteria.isDraft": "false", "api-version": "7.0", "$top": "200"}
    )
    return [pr for pr in data.get("value", []) if not pr.get("isDraft", False)]

def get_updated_prs(since: datetime) -> list[dict]:
    """Return active non-draft PRs whose creation date is >= since."""
    data = ado_get(
        f"git/repositories/{ADO_REPOSITORY}/pullRequests",
        **{"searchCriteria.status": "active", "searchCriteria.isDraft": "false", "api-version": "7.0", "$top": "200"}
    )
    since_utc = since.astimezone(timezone.utc)
    prs = []
    for pr in data.get("value", []):
        updated = pr.get("creationDate") or ""
        try:
            updated_dt = datetime.fromisoformat(updated.replace("Z", "+00:00"))
        except ValueError:
            continue
        if updated_dt >= since_utc and not pr.get("isDraft", False):
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
      "severity":    "critical" | "medium" | "low",
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
    4. KEEPING all critical-severity (critical) comments regardless of confidence.
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
      "severity":    "critical" | "medium" | "low",
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
    # Use bot PAT if configured so comments appear under a service identity,
    # otherwise fall back to personal PAT.
    posting_token = ADO_BOT_PAT if ADO_BOT_PAT else ADO_PAT
    try:
        r = requests.post(
            f"{CLOUD_AGENT_URL}/api/codereview/comments/post",
            json=payload,
            headers={"X-Ado-Access-Token": posting_token},
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

def review_pr(pr: dict, dry_run: bool) -> dict:
    """Review a single PR and return a report entry."""
    pr_id = pr["pullRequestId"]
    title = pr.get("title", "")
    author = pr.get("createdBy", {}).get("displayName", "Unknown")
    print(f"\n{'='*60}")
    print(f"PR #{pr_id}: {title}")
    print(f"Link: {pr_link(pr_id)}")
    print(f"{'='*60}")

    report_entry = {
        "pr_id": pr_id,
        "title": title,
        "author": author,
        "link": pr_link(pr_id),
        "claude_count": 0,
        "cloud_count": 0,
        "consolidated_count": 0,
        "posted_count": 0,
        "skipped_count": 0,
        "posted_comments": [],
        "error": None,
    }

    try:
        # 1. Fetch diff
        print("  [Diff] Fetching changed files...")
        diff = get_pr_diff(pr_id)

        # 2. Claude local review + 3. Cloud agent review (in parallel via threads)
        from concurrent.futures import ThreadPoolExecutor
        with ThreadPoolExecutor(max_workers=2) as executor:
            f_claude = executor.submit(run_claude_review, pr, diff)
            f_cloud  = executor.submit(run_cloud_agent_review, pr_id)
            claude_comments = f_claude.result()
            cloud_comments  = f_cloud.result()

        report_entry["claude_count"] = len(claude_comments)
        report_entry["cloud_count"]  = len(cloud_comments)
        print(f"  [Results] Claude: {len(claude_comments)}  Cloud agent: {len(cloud_comments)}")

        # 4. Consolidate
        consolidated = consolidate_comments(cloud_comments, claude_comments)
        report_entry["consolidated_count"] = len(consolidated)

        # 5. Post — critical and high severity only
        high_only = [c for c in consolidated if c.get("severity", "").lower() in ("critical", "high")]
        print(f"  [Filter] {len(consolidated)} total -> {len(high_only)} critical/high severity")
        result = post_comments(pr_id, high_only, dry_run)

        report_entry["posted_count"]   = result.get("posted", 0)
        report_entry["skipped_count"]  = result.get("skipped", 0)
        report_entry["posted_comments"] = high_only if dry_run else result.get("postedComments", high_only)

    except Exception as e:
        report_entry["error"] = str(e)

    return report_entry


# ---------------------------------------------------------------------------
# Report generation + email
# ---------------------------------------------------------------------------

SEVERITY_COLOR = {"high": "#d73a49", "critical": "#d73a49", "medium": "#e36209", "low": "#6a737d"}
SEVERITY_BADGE = {"high": "#ffeef0", "critical": "#ffeef0", "medium": "#fff5b1", "low": "#f1f8ff"}

def build_html_report(report_entries: list[dict], run_date: str, dry_run: bool) -> str:
    total_prs     = len(report_entries)
    total_posted  = sum(e["posted_count"] for e in report_entries)
    total_skipped = sum(e["skipped_count"] for e in report_entries)
    errors        = [e for e in report_entries if e.get("error")]
    dry_label     = " (DRY RUN)" if dry_run else ""

    rows = ""
    for e in report_entries:
        status = "ERROR" if e.get("error") else ("OK" if not dry_run else "DRY RUN")
        status_color = "#d73a49" if e.get("error") else "#28a745"
        comments_html = ""
        for c in e.get("posted_comments", []):
            sev   = c.get("severity", "medium").lower()
            color = SEVERITY_COLOR.get(sev, "#6a737d")
            badge = SEVERITY_BADGE.get(sev, "#f6f8fa")
            file_path = c.get("filePath", "")
            line      = c.get("startLine", 1)
            text      = c.get("commentText", "").replace("<", "&lt;").replace(">", "&gt;")
            fix       = c.get("suggestedFix", "")
            fix_html  = f'<div style="margin-top:4px;font-size:12px;color:#555"><b>Suggested fix:</b> {fix.replace("<","&lt;").replace(">","&gt;")}</div>' if fix else ""
            comments_html += f"""
            <div style="margin:8px 0;padding:10px 12px;border-left:3px solid {color};background:{badge};border-radius:0 4px 4px 0">
              <div style="font-size:11px;font-weight:600;color:{color};text-transform:uppercase;margin-bottom:4px">{sev}</div>
              <div style="font-size:12px;color:#586069;margin-bottom:4px"><code>{file_path}:{line}</code></div>
              <div style="font-size:13px;color:#24292e">{text}</div>
              {fix_html}
            </div>"""

        if not e.get("posted_comments"):
            comments_html = '<div style="color:#6a737d;font-size:13px;padding:8px 0">No high severity comments posted.</div>'

        error_html = f'<div style="color:#d73a49;font-size:12px;margin-top:6px">Error: {e["error"]}</div>' if e.get("error") else ""

        rows += f"""
        <tr>
          <td style="padding:16px;vertical-align:top;border-bottom:1px solid #e1e4e8">
            <div style="margin-bottom:4px">
              <a href="{e['link']}" style="font-weight:600;color:#0366d6;text-decoration:none">PR #{e['pr_id']}: {e['title']}</a>
            </div>
            <div style="font-size:12px;color:#586069;margin-bottom:8px">Author: {e['author']}</div>
            <div style="font-size:12px;color:#586069;margin-bottom:8px">
              Claude: {e['claude_count']} &nbsp;|&nbsp; Cloud Agent: {e['cloud_count']} &nbsp;|&nbsp;
              Consolidated: {e['consolidated_count']} &nbsp;|&nbsp;
              <b>Posted: {e['posted_count']}</b> &nbsp; Skipped (dup): {e['skipped_count']}
            </div>
            {comments_html}
            {error_html}
          </td>
          <td style="padding:16px;vertical-align:top;border-bottom:1px solid #e1e4e8;text-align:center;white-space:nowrap">
            <span style="color:{status_color};font-weight:600;font-size:12px">{status}</span>
          </td>
        </tr>"""

    error_summary = ""
    if errors:
        error_summary = f'<div style="background:#ffeef0;border:1px solid #fca5a5;border-radius:6px;padding:12px;margin-bottom:16px"><b>{len(errors)} PR(s) had errors.</b></div>'

    html = f"""<!DOCTYPE html>
<html>
<head><meta charset="utf-8"><title>Daily PR Review Report{dry_label}</title></head>
<body style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#f6f8fa;margin:0;padding:24px">
  <div style="max-width:900px;margin:0 auto;background:#fff;border:1px solid #e1e4e8;border-radius:8px;overflow:hidden">
    <div style="background:#24292e;padding:20px 24px">
      <h1 style="color:#fff;margin:0;font-size:20px">AI Code Review — Daily Report{dry_label}</h1>
      <div style="color:#8b949e;font-size:13px;margin-top:4px">{run_date} &nbsp;·&nbsp; {ADO_PROJECT} / {ADO_REPOSITORY}</div>
    </div>
    <div style="padding:20px 24px;background:#f6f8fa;border-bottom:1px solid #e1e4e8;display:flex;gap:32px">
      <div><div style="font-size:28px;font-weight:700;color:#24292e">{total_prs}</div><div style="font-size:12px;color:#586069">PRs Reviewed</div></div>
      <div><div style="font-size:28px;font-weight:700;color:#d73a49">{total_posted}</div><div style="font-size:12px;color:#586069">Comments Posted</div></div>
      <div><div style="font-size:28px;font-weight:700;color:#6a737d">{total_skipped}</div><div style="font-size:12px;color:#586069">Duplicates Skipped</div></div>
    </div>
    <div style="padding:20px 24px">
      {error_summary}
      <table style="width:100%;border-collapse:collapse">
        <thead>
          <tr style="background:#f6f8fa">
            <th style="padding:10px 16px;text-align:left;font-size:12px;color:#586069;border-bottom:2px solid #e1e4e8">Pull Request & Comments</th>
            <th style="padding:10px 16px;text-align:center;font-size:12px;color:#586069;border-bottom:2px solid #e1e4e8;width:80px">Status</th>
          </tr>
        </thead>
        <tbody>{rows}</tbody>
      </table>
    </div>
    <div style="padding:16px 24px;background:#f6f8fa;border-top:1px solid #e1e4e8;font-size:11px;color:#586069">
      Generated by AI Code Review Agent &nbsp;·&nbsp; Claude Sonnet + GPT-4 dual-model pipeline
    </div>
  </div>
</body>
</html>"""
    return html


def save_report(html: str, run_date: str) -> Path:
    REPORTS_DIR.mkdir(exist_ok=True)
    path = REPORTS_DIR / f"pr_review_{run_date.replace(' ', '_').replace(':', '-')}.html"
    path.write_text(html, encoding="utf-8")
    print(f"\n[Report] Saved to {path}")
    return path


def send_email_report(html: str, run_date: str, pr_count: int, comment_count: int):
    if not all([EMAIL_FROM, EMAIL_TO, EMAIL_SMTP_USER, EMAIL_SMTP_PASS]):
        print("[Email] Skipped — REPORT_EMAIL_* not configured in .env")
        return

    recipients = [r.strip() for r in EMAIL_TO.split(",") if r.strip()]
    subject = f"AI Code Review Report — {run_date} ({pr_count} PRs, {comment_count} comments posted)"

    msg = MIMEMultipart("alternative")
    msg["Subject"] = subject
    msg["From"]    = EMAIL_FROM
    msg["To"]      = ", ".join(recipients)
    msg.attach(MIMEText(html, "html", "utf-8"))

    try:
        print(f"[Email] Sending to {', '.join(recipients)}...")
        with smtplib.SMTP(EMAIL_SMTP_HOST, EMAIL_SMTP_PORT) as server:
            server.ehlo()
            server.starttls()
            server.login(EMAIL_SMTP_USER, EMAIL_SMTP_PASS)
            server.sendmail(EMAIL_FROM, recipients, msg.as_string())
        print("[Email] Sent successfully")
    except Exception as e:
        print(f"[Email] Failed: {e}")


def main():
    parser = argparse.ArgumentParser(description="Daily PR review — dual model + consolidate + post")
    parser.add_argument("--pr",      type=int, help="Review a specific PR by ID")
    parser.add_argument("--dry-run", action="store_true", help="Review and consolidate but don't post")
    parser.add_argument("--all",     action="store_true", help="Review ALL active non-draft PRs (ignores --hours)")
    parser.add_argument("--hours",   type=int, default=LOOKBACK_HOURS, help="Look-back window in hours (default 24)")
    args = parser.parse_args()

    if not ADO_PAT:
        print("ERROR: ADO_PAT is not set. Add it to .env or set as environment variable.")
        sys.exit(1)

    run_date = datetime.now().strftime("%Y-%m-%d %H:%M")
    report_entries = []

    if args.pr:
        pr = get_pr_by_id(args.pr)
        entry = review_pr(pr, args.dry_run)
        report_entries.append(entry)
    else:
        if args.all:
            print("Fetching ALL active non-draft PRs...")
            prs = get_all_active_prs()
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
            entry = review_pr(pr, args.dry_run)
            report_entries.append(entry)

    # Generate report
    if report_entries:
        html = build_html_report(report_entries, run_date, args.dry_run)
        save_report(html, run_date)
        total_posted = sum(e["posted_count"] for e in report_entries)
        send_email_report(html, run_date, len(report_entries), total_posted)

    print("\nDone.")


if __name__ == "__main__":
    main()
