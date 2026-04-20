#!/usr/bin/env bash
# Phase 9 T007 — identity smoke script scaffold.
# Full walkthrough lands in Phase 9 Polish (post-US7). This scaffold just
# provides the evidence directory + per-step header/logging helpers so
# subsequent sessions can drop steps in.

set -euo pipefail

# --------- evidence layout ---------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EVIDENCE_ROOT="${SCRIPT_DIR}/scripts/_evidence/identity"
mkdir -p "${EVIDENCE_ROOT}"

STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
CORRELATION_ID="${IDENTITY_SMOKE_CORRELATION_ID:-$(uuidgen | tr '[:upper:]' '[:lower:]')}"
echo "${STARTED_AT}" > "${EVIDENCE_ROOT}/started_at.txt"
echo "${CORRELATION_ID}" > "${EVIDENCE_ROOT}/correlation_id.txt"

# --------- helpers ---------
header() {
  local step_id="$1"
  local description="$2"
  echo ""
  echo "=================================================================="
  echo "[${step_id}] ${description}"
  echo "  correlation_id=${CORRELATION_ID}"
  echo "  evidence_dir=${EVIDENCE_ROOT}"
  echo "=================================================================="
}

record_body() {
  # record_body <step_id> <body-string>
  local step_id="$1"
  local body="$2"
  printf '%s' "${body}" > "${EVIDENCE_ROOT}/${step_id}.body"
  touch "${EVIDENCE_ROOT}/${step_id}.ok"
}

record_exit_code() {
  local code="$1"
  echo "${code}" > "${EVIDENCE_ROOT}/exit_code"
}

trap 'record_exit_code "$?"' EXIT

# --------- steps (populated in later sessions) ---------
header "T007" "scaffold established"
record_body "T007" "identity smoke scaffold ready; walkthrough steps land in Polish"

COMPLETED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "${COMPLETED_AT}" > "${EVIDENCE_ROOT}/completed_at.txt"
echo ""
echo "Identity smoke scaffold OK — evidence under ${EVIDENCE_ROOT}"
