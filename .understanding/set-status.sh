#!/usr/bin/env bash
# set-status.sh — write the `understanding-check` commit status on a PR head commit.
#
# Single source of truth for the gate's context string AND the unblock copy the
# engineer sees. Used in two places:
#   • CI (the gate workflow) arms `pending` on every PR push.
#   • The local /understanding-check skill flips it to `success` after the interview.
set -euo pipefail

# Configuration — precedence: built-in default < .understanding/config < environment.
# The config file lives next to this script, so it resolves regardless of CWD.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_FILE="${UNDERSTANDING_CONFIG:-$SCRIPT_DIR/config}"

# Capture env-provided overrides before sourcing the config (so env wins).
_env_skill="${UNDERSTANDING_SKILL:-}"
_env_context="${UNDERSTANDING_CONTEXT:-}"
_env_docs="${UNDERSTANDING_DOCS_URL:-}"

# shellcheck disable=SC1090
[[ -f "$CONFIG_FILE" ]] && source "$CONFIG_FILE"

[[ -n "$_env_skill" ]]   && UNDERSTANDING_SKILL="$_env_skill"
[[ -n "$_env_context" ]] && UNDERSTANDING_CONTEXT="$_env_context"
[[ -n "$_env_docs" ]]    && UNDERSTANDING_DOCS_URL="$_env_docs"

SKILL="${UNDERSTANDING_SKILL:-understanding-check}"
CONTEXT="${UNDERSTANDING_CONTEXT:-understanding-check}"
DOCS_URL="${UNDERSTANDING_DOCS_URL:-https://github.com/Jeffrharr/ClaudeCIQuestions#unblocking-a-pr}"

usage() {
  cat <<'EOF'
Usage: set-status.sh <pending|success|failure> [--sha <sha>] [--pr <num>] [--repo <owner/name>]

Writes the `understanding-check` commit status on a pull request's head commit.

  pending   gate is armed; the PR cannot merge until cleared (set by CI on each push)
  success   understanding confirmed; unblocks merge (set by /understanding-check)
  failure   explicitly mark the check failed

Targeting (first match wins):
  --sha <sha>        write the status to this commit SHA
  --pr  <num>        resolve the head SHA from this PR number
  (default)          resolve the open PR for the current branch; else HEAD

  --repo owner/name  override the repo (default: $GITHUB_REPOSITORY, else `gh repo view`)

Config (.understanding/config, overridable by env):
  UNDERSTANDING_SKILL       slash command shown in the unblock message (default: understanding-check)
  UNDERSTANDING_CONTEXT     status check name; must match branch protection (default: understanding-check)
  UNDERSTANDING_DOCS_URL    the "Details" link on the status

Env:
  GH_TOKEN / GITHUB_TOKEN   token used by `gh` (needs `statuses: write`)
  UNDERSTANDING_CONFIG      path to the config file (default: <script dir>/config)
EOF
}

[[ $# -ge 1 ]] || { usage; exit 2; }

STATE="$1"; shift
case "$STATE" in
  pending|success|failure) ;;
  -h|--help) usage; exit 0 ;;
  *) echo "error: invalid state '$STATE'" >&2; usage; exit 2 ;;
esac

SHA=""; PR=""; REPO=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --sha)  SHA="$2";  shift 2 ;;
    --pr)   PR="$2";   shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "error: unknown arg '$1'" >&2; usage; exit 2 ;;
  esac
done

command -v gh >/dev/null || { echo "error: GitHub CLI (gh) not found" >&2; exit 1; }

# Resolve repo: explicit flag → CI env → current checkout.
[[ -n "$REPO" ]] || REPO="${GITHUB_REPOSITORY:-}"
[[ -n "$REPO" ]] || REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

# Resolve SHA: explicit flag → from PR number → current-branch PR → HEAD.
if [[ -z "$SHA" && -n "$PR" ]]; then
  SHA="$(gh pr view "$PR" --repo "$REPO" --json headRefOid -q .headRefOid)"
fi
if [[ -z "$SHA" ]]; then
  SHA="$(gh pr view --repo "$REPO" --json headRefOid -q .headRefOid 2>/dev/null || true)"
fi
if [[ -z "$SHA" ]]; then
  SHA="$(git rev-parse HEAD 2>/dev/null || true)"
fi
[[ -n "$SHA" ]] || { echo "error: could not resolve a commit SHA (pass --sha or --pr)" >&2; exit 1; }

# Unblock copy — the single place that decides what the engineer reads on the check.
case "$STATE" in
  pending) DESC="Run /$SKILL in Claude Code to unblock this PR" ;;
  success) DESC="Understanding confirmed via /$SKILL" ;;
  failure) DESC="Understanding check not completed — run /$SKILL" ;;
esac

gh api -X POST "repos/$REPO/statuses/$SHA" \
  -f state="$STATE" \
  -f context="$CONTEXT" \
  -f description="$DESC" \
  -f target_url="$DOCS_URL" >/dev/null

echo "set $CONTEXT=$STATE on $REPO@${SHA:0:12}"
