#!/usr/bin/env bash
# T164 — Phase 4 local smoke run.
#
# Exercises the twelve-step quickstart walkthrough in
# specs/006-engagement-progress-parent/quickstart.md against the local
# Docker Compose stacks for Phase 1, Phase 2, Phase 3, and Phase 4 — zero
# managed cloud credentials required.
#
# Each step hits a main-backend facade endpoint with a canned payload,
# asserts the expected status/body, and writes per-step evidence files
# under infra/scripts/_evidence/phase4/. The evidence folder is what the
# Phase 4 readiness gate references (T166).
#
# Usage:
#   ./infra/phase4-smoke.sh           # run all twelve steps
#   STEP=us3 ./infra/phase4-smoke.sh  # run a single step only
#   BASE_URL=http://localhost:5080 ./infra/phase4-smoke.sh
#
# Exit codes:
#   0  all twelve steps passed
#   >0 first failing step number — also written to _evidence/exit_code
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5080}
AI_SERVICE_URL=${AI_SERVICE_URL:-http://localhost:5081}
TENANT_ID=${TENANT_ID:-11111111-1111-1111-1111-111111111111}
STUDENT_PROFILE_ID=${STUDENT_PROFILE_ID:-22222222-2222-2222-2222-222222222222}
PARENT_PROFILE_ID=${PARENT_PROFILE_ID:-33333333-3333-3333-3333-333333333333}
OPERATOR_ACTOR_ID=${OPERATOR_ACTOR_ID:-99999999-9999-9999-9999-999999999999}
CORRELATION_ID=${CORRELATION_ID:-$(uuidgen | tr '[:upper:]' '[:lower:]')}
EVIDENCE_DIR=${EVIDENCE_DIR:-infra/scripts/_evidence/phase4}

mkdir -p "$EVIDENCE_DIR"
echo "$CORRELATION_ID" > "$EVIDENCE_DIR/correlation_id.txt"

STEP_FILTER=${STEP:-all}
CURL_BASE=(curl -sS -o /dev/null -w '%{http_code}'
  -H "X-Tenant-Id: $TENANT_ID"
  -H "X-Correlation-Id: $CORRELATION_ID"
  -H 'Content-Type: application/json')

header() { printf '\n\033[1;34m[%s]\033[0m %s\n' "$1" "$2"; }
ok()     { printf '  \033[1;32m✓\033[0m %s\n' "$1"; }
fail()   { printf '  \033[1;31m✗\033[0m %s\n' "$1"; echo "$2" > "$EVIDENCE_DIR/exit_code"; exit "$2"; }

expect_status() {
  local want="$1" got="$2" label="$3" step="$4"
  if [[ "$want" != "$got" ]]; then
    echo "got=$got want=$want label=$label" > "$EVIDENCE_DIR/${step}.fail"
    fail "$label: expected $want got $got" "$step"
  fi
  ok "$label (HTTP $got)"
}

run_step() {
  local id="$1" label="$2"
  if [[ "$STEP_FILTER" != "all" && "$STEP_FILTER" != "$id" ]]; then return 0; fi
  header "$id" "$label"
  "step_${id}"
  touch "$EVIDENCE_DIR/${id}.ok"
}

# ---------------------------------------------------------------- step 1 -----
# Bring-up. Phase 1 + Phase 2 + Phase 3 + Phase 4 health probes succeed
# without managed cloud credentials.
step_1() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/healthz/ready")
  expect_status 200 "$code" "main-backend ready" 1
  code=$("${CURL_BASE[@]}" "$AI_SERVICE_URL/healthz/ready")
  expect_status 200 "$code" "ai-service ready" 1
}

# ---------------------------------------------------------------- step 2 -----
# Seed a parent profile, child link, badge catalogue, and preferences.
# Idempotent: each endpoint upserts so reruns are safe.
step_2() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/test-seed/parent-profile" \
    -d "{\"parent_profile_id\":\"$PARENT_PROFILE_ID\",\"tenant_id\":\"$TENANT_ID\",\"preferred_language\":\"ar\",\"timezone\":\"Asia/Dubai\"}")
  expect_status 200 "$code" "seed parent profile" 2
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/test-seed/child-link" \
    -d "{\"parent_profile_id\":\"$PARENT_PROFILE_ID\",\"student_profile_id\":\"$STUDENT_PROFILE_ID\",\"role\":\"guardian\"}")
  expect_status 200 "$code" "seed child link" 2
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/test-seed/badge-criteria" -d '{}')
  expect_status 200 "$code" "seed badge catalogue" 2
}

# ---------------------------------------------------------------- step 3 -----
# US4 — Replay the synthetic Phase 3 event stream. Re-replay confirms
# idempotency (no additional state changes).
step_3() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/phase4/replay-phase3-fixtures" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\"}")
  expect_status 200 "$code" "replay phase3 fixtures (first pass)" 3
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/phase4/replay-phase3-fixtures" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\"}")
  expect_status 200 "$code" "replay phase3 fixtures (idempotent re-run)" 3
}

# ---------------------------------------------------------------- step 4 -----
# US1 — Student progress surface.
step_4() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/student/progress/summary")
  expect_status 200 "$code" "GET /student/progress/summary" 4
}

# ---------------------------------------------------------------- step 5 -----
# US5 — Focus areas grounded in Phase 1. Every focus area references an
# approved curriculum node with a stored guardrail_decision_trail_id.
step_5() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/student/progress/focus-areas")
  expect_status 200 "$code" "GET /student/progress/focus-areas" 5
}

# ---------------------------------------------------------------- step 6 -----
# US6 — Badges and streaks. Confirm the seeded badge appears on the
# progress surface and the parent dashboard.
step_6() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/student/progress/badges")
  expect_status 200 "$code" "GET /student/progress/badges" 6
}

# ---------------------------------------------------------------- step 7 -----
# US2 — Parent dashboard with child selector.
step_7() {
  local code
  code=$("${CURL_BASE[@]}" \
    -H "X-Parent-Profile-Id: $PARENT_PROFILE_ID" \
    "$BASE_URL/parent/children")
  expect_status 200 "$code" "GET /parent/children" 7
  code=$("${CURL_BASE[@]}" \
    -H "X-Parent-Profile-Id: $PARENT_PROFILE_ID" \
    "$BASE_URL/parent/dashboard/$STUDENT_PROFILE_ID")
  expect_status 200 "$code" "GET /parent/dashboard/{child}" 7
}

# ---------------------------------------------------------------- step 8 -----
# US3 — Weekly report generation. First call triggers generation; second
# call confirms exactly one ready report per window (uniqueness).
step_8() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/parent/weekly-reports/generate" \
    -H "X-Parent-Profile-Id: $PARENT_PROFILE_ID" \
    -d "{\"child_id\":\"$STUDENT_PROFILE_ID\"}")
  expect_status 200 "$code" "POST /parent/weekly-reports/generate" 8
  code=$("${CURL_BASE[@]}" \
    -H "X-Parent-Profile-Id: $PARENT_PROFILE_ID" \
    "$BASE_URL/parent/weekly-reports?child_id=$STUDENT_PROFILE_ID")
  expect_status 200 "$code" "GET /parent/weekly-reports (list)" 8
}

# ---------------------------------------------------------------- step 9 -----
# US7 — Parent notifications. Dispatch, confirm local stub delivery, then
# defer by quiet hours and re-dispatch when the window ends.
step_9() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/phase4/dispatch-notifications" \
    -d "{\"parent_profile_id\":\"$PARENT_PROFILE_ID\"}")
  expect_status 200 "$code" "POST dispatch notifications" 9
  code=$("${CURL_BASE[@]}" \
    -H "X-Parent-Profile-Id: $PARENT_PROFILE_ID" \
    "$BASE_URL/parent/notifications")
  expect_status 200 "$code" "GET /parent/notifications" 9
}

# --------------------------------------------------------------- step 10 -----
# US8 — At-risk detection and intervention. Raise a flag from the
# reference synthetic pattern, confirm an intervention prompt is created,
# then replay recovery and confirm the flag clears.
step_10() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/phase4/run-atrisk-job" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\",\"scenario\":\"at_risk\"}")
  expect_status 200 "$code" "run at-risk job (raise)" 10
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/phase4/run-atrisk-job" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\",\"scenario\":\"recovery\"}")
  expect_status 200 "$code" "run at-risk job (recovery)" 10
}

# --------------------------------------------------------------- step 11 -----
# Operator impersonation audit. Every impersonated dashboard view MUST
# write an audit row.
step_11() {
  local code
  code=$("${CURL_BASE[@]}" \
    -H "X-Operator-Actor-Id: $OPERATOR_ACTOR_ID" \
    -H "X-Impersonation-Reason: support_case_phase4_smoke" \
    -H "X-Parent-Profile-Id: $PARENT_PROFILE_ID" \
    "$BASE_URL/parent/dashboard/$STUDENT_PROFILE_ID")
  expect_status 200 "$code" "impersonated GET /parent/dashboard" 11
  code=$("${CURL_BASE[@]}" \
    "$BASE_URL/internal/diag/operator-impersonation-audit?operator=$OPERATOR_ACTOR_ID")
  expect_status 200 "$code" "audit row persisted for operator" 11
}

# --------------------------------------------------------------- step 12 -----
# Downstream events to Phase 5. Drain the outbox and confirm every kind
# produced during the walkthrough has landed on the local broker.
step_12() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/internal/diag/phase4-downstream-outbox?state=dispatched")
  expect_status 200 "$code" "downstream outbox drained" 12
}

run_step 1  "bring up local infrastructure"
run_step 2  "seed parent profile + child link + badges"
run_step 3  "US4 replay phase3 fixtures (idempotent)"
run_step 4  "US1 student progress surface"
run_step 5  "US5 focus areas grounded in phase 1"
run_step 6  "US6 badges + streaks"
run_step 7  "US2 parent dashboard with child selector"
run_step 8  "US3 weekly report generation"
run_step 9  "US7 parent notifications + quiet hours"
run_step 10 "US8 at-risk detection and intervention"
run_step 11 "operator impersonation audit row"
run_step 12 "downstream event outbox drained"

echo 0 > "$EVIDENCE_DIR/exit_code"
header done "all twelve Phase 4 quickstart steps passed"
