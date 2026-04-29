#!/usr/bin/env bash
# Phase 9 Phase 5 — credential-management local smoke run.
#
# Exercises the Phase 9 credential-flow walkthrough against the local
# Docker Compose stack. Zero managed cloud credentials required; the
# only prerequisite is that main-backend is reachable at $BASE_URL.
#
# Steps:
#   T007  scaffold (preserved for backwards-compat)
#   IDS01 anti-enum: forgot-password unknown email → 200 generic
#   IDS02 anti-enum: forgot-password known email   → 200 generic
#   IDS03 reset-password with garbage token        → 4xx token_invalid
#   IDS04 verify-email with garbage token          → 4xx token_invalid
#   IDS05 parent credential reauth without JWT     → 401 (route is gated)
#   IDS06 parent reset-pin without JWT             → 401 (route is gated)
#   IDS07 parent add-pin without JWT               → 401 (route is gated)
#   IDS08 parent upgrade-to-password without JWT   → 401 (route is gated)
#   IDS09 child change-password without JWT        → 401
#
# Usage:
#   ./infra/identity-smoke.sh                       # run all steps
#   STEP=IDS05 ./infra/identity-smoke.sh            # run a single step
#   BASE_URL=http://localhost:5080 ./infra/identity-smoke.sh
#   KNOWN_EMAIL=parent@example.com ./infra/identity-smoke.sh
#
# Exit codes:
#   0   all steps passed
#   >0  first failing step ID — see ${EVIDENCE_ROOT}/${step}.fail
set -euo pipefail

# --------- evidence layout ---------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EVIDENCE_ROOT="${EVIDENCE_DIR:-${SCRIPT_DIR}/scripts/_evidence/identity}"
mkdir -p "${EVIDENCE_ROOT}"

BASE_URL=${BASE_URL:-http://localhost:5080}
KNOWN_EMAIL=${KNOWN_EMAIL:-parent@example.com}
UNKNOWN_EMAIL=${UNKNOWN_EMAIL:-nobody-$(date +%s)@example.com}
CHILD_ID=${CHILD_ID:-00000000-0000-0000-0000-00000000c111}

STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
CORRELATION_ID="${IDENTITY_SMOKE_CORRELATION_ID:-$(uuidgen | tr '[:upper:]' '[:lower:]')}"
echo "${STARTED_AT}" > "${EVIDENCE_ROOT}/started_at.txt"
echo "${CORRELATION_ID}" > "${EVIDENCE_ROOT}/correlation_id.txt"

STEP_FILTER=${STEP:-all}

# --------- helpers ---------
header() {
  local step_id="$1"
  local description="$2"
  printf '\n\033[1;34m[%s]\033[0m %s\n' "${step_id}" "${description}"
  echo "  correlation_id=${CORRELATION_ID}"
  echo "  evidence_dir=${EVIDENCE_ROOT}"
}

ok()   { printf '  \033[1;32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[1;31m✗\033[0m %s\n' "$1"; echo "${2:-1}" > "${EVIDENCE_ROOT}/exit_code"; exit "${2:-1}"; }

record_body() {
  local step_id="$1"
  local body="$2"
  printf '%s' "${body}" > "${EVIDENCE_ROOT}/${step_id}.body"
  touch "${EVIDENCE_ROOT}/${step_id}.ok"
}

run_step() {
  local id="$1"
  if [[ "${STEP_FILTER}" != "all" && "${STEP_FILTER}" != "${id}" ]]; then
    return 1
  fi
  return 0
}

# Run an endpoint check. Captures body to ${id}.body and the HTTP status to
# ${id}.status. If the actual status doesn't match any of the expected codes,
# writes ${id}.fail and exits. Sets ok marker on success.
expect_one_of() {
  local id="$1" method="$2" url="$3" data="$4"; shift 4
  local resp body
  if [[ -n "$data" ]]; then
    resp=$(curl -sS -o "${EVIDENCE_ROOT}/${id}.body" -w '%{http_code}' \
      -X "$method" -H 'Content-Type: application/json' \
      -H "X-Correlation-Id: ${CORRELATION_ID}" \
      -d "$data" "${BASE_URL}${url}" || true)
  else
    resp=$(curl -sS -o "${EVIDENCE_ROOT}/${id}.body" -w '%{http_code}' \
      -X "$method" -H "X-Correlation-Id: ${CORRELATION_ID}" \
      "${BASE_URL}${url}" || true)
  fi
  echo "$resp" > "${EVIDENCE_ROOT}/${id}.status"
  for want in "$@"; do
    if [[ "$resp" == "$want" ]]; then
      touch "${EVIDENCE_ROOT}/${id}.ok"
      ok "${id} got HTTP ${resp} (expected one of: $*)"
      return 0
    fi
  done
  body=$(cat "${EVIDENCE_ROOT}/${id}.body" 2>/dev/null || true)
  echo "got=${resp} want_one_of=$*" > "${EVIDENCE_ROOT}/${id}.fail"
  fail "${id}: HTTP ${resp} (expected one of $*) — body=${body:0:200}"
}

trap 'echo "$?" > "${EVIDENCE_ROOT}/exit_code"' EXIT

# --------- T007 scaffold (preserved for back-compat) ---------
if run_step "T007"; then
  header "T007" "scaffold established"
  record_body "T007" "identity smoke scaffold ready; credential walkthrough below"
fi

# --------- IDS01 — anti-enumeration on unknown email ---------
if run_step "IDS01"; then
  header "IDS01" "forgot-password with UNKNOWN email returns generic 200"
  expect_one_of "IDS01" POST "/api/auth/forgot-password" \
    "{\"email\":\"${UNKNOWN_EMAIL}\",\"ipAddress\":\"127.0.0.1\",\"correlationId\":\"${CORRELATION_ID}\"}" \
    200
fi

# --------- IDS02 — anti-enumeration on known email ---------
if run_step "IDS02"; then
  header "IDS02" "forgot-password with KNOWN email returns generic 200"
  expect_one_of "IDS02" POST "/api/auth/forgot-password" \
    "{\"email\":\"${KNOWN_EMAIL}\",\"ipAddress\":\"127.0.0.1\",\"correlationId\":\"${CORRELATION_ID}\"}" \
    200
fi

# --------- IDS03 — reset-password with garbage token ---------
if run_step "IDS03"; then
  header "IDS03" "reset-password with garbage token returns 4xx"
  expect_one_of "IDS03" POST "/api/auth/reset-password" \
    "{\"token\":\"garbage-token-not-real\",\"newPassword\":\"AnyNew-9!\",\"correlationId\":\"${CORRELATION_ID}\"}" \
    400 401 422
fi

# --------- IDS04 — verify-email with garbage token ---------
if run_step "IDS04"; then
  header "IDS04" "verify-email with garbage token returns 4xx"
  expect_one_of "IDS04" POST "/api/auth/verify-email" \
    "{\"token\":\"garbage-token-not-real\"}" \
    400 401 404 422
fi

# --------- IDS05 — parent credential reauth without JWT ---------
if run_step "IDS05"; then
  header "IDS05" "parent /credential/reauth without JWT is rejected (401)"
  expect_one_of "IDS05" POST "/api/auth/parent/credential/reauth" \
    "{\"password\":\"any\"}" \
    401
fi

# --------- IDS06 — parent reset-pin without JWT ---------
if run_step "IDS06"; then
  header "IDS06" "parent /children/{id}/reset-pin without JWT is rejected (401)"
  expect_one_of "IDS06" POST "/api/auth/parent/children/${CHILD_ID}/reset-pin" \
    "{\"newPin\":\"1234\"}" \
    401
fi

# --------- IDS07 — parent add-pin without JWT ---------
if run_step "IDS07"; then
  header "IDS07" "parent /children/{id}/add-pin without JWT is rejected (401)"
  expect_one_of "IDS07" POST "/api/auth/parent/children/${CHILD_ID}/add-pin" \
    "{\"newPin\":\"1234\"}" \
    401
fi

# --------- IDS08 — parent upgrade-to-password without JWT ---------
if run_step "IDS08"; then
  header "IDS08" "parent /children/{id}/upgrade-to-password without JWT is rejected (401)"
  expect_one_of "IDS08" POST "/api/auth/parent/children/${CHILD_ID}/upgrade-to-password" \
    "{\"newPassword\":\"AnyNew-9!\"}" \
    401
fi

# --------- IDS09 — child change-password without JWT ---------
if run_step "IDS09"; then
  header "IDS09" "child /change-password without JWT is rejected (401)"
  expect_one_of "IDS09" POST "/api/auth/change-password" \
    "{\"currentPassword\":\"x\",\"newPassword\":\"AnyNew-9!\"}" \
    401
fi

COMPLETED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "${COMPLETED_AT}" > "${EVIDENCE_ROOT}/completed_at.txt"
echo "0" > "${EVIDENCE_ROOT}/exit_code"
printf '\nIdentity credential smoke OK — evidence under %s\n' "${EVIDENCE_ROOT}"
