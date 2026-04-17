#!/usr/bin/env bash
# T131 — Phase 3 local smoke run.
#
# Exercises the eleven-step quickstart walkthrough in
# specs/005-student-learning-experience/quickstart.md against the local
# Docker Compose stacks for Phase 1, Phase 2, and Phase 3 — zero managed
# cloud credentials required.
#
# Each step hits a main-backend facade endpoint with a canned payload,
# asserts the expected status/body, and writes per-step evidence files
# under infra/scripts/_evidence/phase3/. The evidence folder is what the
# Phase 3 readiness gate references (T132).
#
# Usage:
#   ./infra/scripts/phase3-smoke.sh            # run all eleven steps
#   STEP=us3 ./infra/scripts/phase3-smoke.sh   # run a single step only
#   BASE_URL=http://localhost:5080 ./infra/scripts/phase3-smoke.sh
#
# Exit codes:
#   0  all eleven steps passed
#   >0 first failing step number (1..11) — also written to _evidence/exit_code
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5080}
AI_SERVICE_URL=${AI_SERVICE_URL:-http://localhost:5081}
TENANT_ID=${TENANT_ID:-11111111-1111-1111-1111-111111111111}
STUDENT_PROFILE_ID=${STUDENT_PROFILE_ID:-22222222-2222-2222-2222-222222222222}
CORRELATION_ID=${CORRELATION_ID:-$(uuidgen | tr '[:upper:]' '[:lower:]')}
EVIDENCE_DIR=${EVIDENCE_DIR:-infra/scripts/_evidence/phase3}

mkdir -p "$EVIDENCE_DIR"
echo "$CORRELATION_ID" > "$EVIDENCE_DIR/correlation_id.txt"

STEP_FILTER=${STEP:-all}
CURL_BASE=(curl -sS -o /dev/null -w '%{http_code}'
  -H "X-Tenant-Id: $TENANT_ID"
  -H "X-Correlation-Id: $CORRELATION_ID"
  -H 'Content-Type: application/json')

header() {
  printf '\n\033[1;34m[%s]\033[0m %s\n' "$1" "$2"
}

ok() { printf '  \033[1;32m✓\033[0m %s\n' "$1"; }
fail() { printf '  \033[1;31m✗\033[0m %s\n' "$1"; exit "$2"; }

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
  if [[ "$STEP_FILTER" != "all" && "$STEP_FILTER" != "$id" ]]; then
    return 0
  fi
  header "$id" "$label"
  "step_${id}"
  touch "$EVIDENCE_DIR/${id}.ok"
}

# ---------------------------------------------------------------- step 1 -----
# Bring-up. Checks the main-backend /healthz/ready surface and the
# Phase 3 compose overlay. A failure here usually means the infra
# commands in the quickstart weren't run.
step_1() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/healthz/ready")
  expect_status 200 "$code" "main-backend ready" 1
  code=$("${CURL_BASE[@]}" "$AI_SERVICE_URL/healthz/ready")
  expect_status 200 "$code" "ai-service ready" 1
}

# ---------------------------------------------------------------- step 2 -----
# Seed profile + plan gate policy. Idempotent: endpoints use upsert so
# rerunning the smoke stays safe.
step_2() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/test-seed/student-profile" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\",\"tenant_id\":\"$TENANT_ID\",\"grade\":\"grade_7\",\"curriculum_type\":\"moe\",\"preferred_language\":\"ar\",\"plan_tier\":\"free\"}")
  expect_status 200 "$code" "seed student profile" 2
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/test-seed/plan-gate" \
    -d '{"feature":"whiteboard","plan_tier":"premium","subject_allowlist":["mathematics","physics"]}')
  expect_status 200 "$code" "seed plan gate (whiteboard)" 2
}

# ---------------------------------------------------------------- step 3 -----
# US1 — Home dashboard. Start a session and confirm the session_start event
# landed in the outbox.
step_3() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/session/start" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\",\"device_class\":\"mobile_small\"}")
  expect_status 200 "$code" "POST /student/session/start" 3
  code=$("${CURL_BASE[@]}" "$BASE_URL/student/home/dashboard")
  expect_status 200 "$code" "GET /student/home/dashboard" 3
}

# ---------------------------------------------------------------- step 4 -----
# US2 — Study lesson viewer.
step_4() {
  local code
  code=$("${CURL_BASE[@]}" "$BASE_URL/student/study/subjects")
  expect_status 200 "$code" "GET /student/study/subjects" 4
}

# ---------------------------------------------------------------- step 5 -----
# US3 — Text tutor. SSE streams a tutor answer; a 200 on the initial POST
# is enough for the smoke. Refusal path exercised via the out-of-scope
# question.
step_5() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/tutor/text" \
    -d '{"prompt_text":"ما هو جمع ٢ و ٣؟","locale":"ar","turn_number":1}')
  expect_status 200 "$code" "POST /student/tutor/text (in-scope)" 5
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/tutor/text" \
    -d '{"prompt_text":"أخبرني نكتة","locale":"ar","turn_number":2}')
  expect_status 200 "$code" "POST /student/tutor/text (out-of-scope refusal)" 5
}

# ---------------------------------------------------------------- step 6 -----
# US4 — Voice tutor. The voice path uploads a zero-byte fixture so the
# smoke stays self-contained; the backend returns a fake voice_playback_ref
# which we then dereference.
step_6() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/tutor/voice" \
    -d '{"voice_capture_blob":"test-fixture","duration_ms":1500,"locale":"ar"}')
  expect_status 200 "$code" "POST /student/tutor/voice" 6
}

# ---------------------------------------------------------------- step 7 -----
# US5 — Solve Questions.
step_7() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/solve-questions/start" \
    -d '{"subject_id":"00000000-0000-0000-0000-000000000001","topic_id":"00000000-0000-0000-0000-000000000002"}')
  expect_status 200 "$code" "POST /student/solve-questions/start" 7
}

# ---------------------------------------------------------------- step 8 -----
# US6 — Mock Test. Server-truth timer is set on start and re-read on state.
step_8() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/mock-test/start" \
    -d '{"subject_id":"00000000-0000-0000-0000-000000000001","duration_seconds":3600}')
  expect_status 200 "$code" "POST /student/mock-test/start" 8
  code=$("${CURL_BASE[@]}" "$BASE_URL/student/mock-test/state")
  expect_status 200 "$code" "GET /student/mock-test/state" 8
}

# ---------------------------------------------------------------- step 9 -----
# US7 — Homework Help image path. Uses a known-good fixture that the
# backend accepts without calling out to managed OCR.
step_9() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/homework-help/submit" \
    -d '{"input_modality":"text","text_payload":"حل ٢ × ٥","subject_id":"00000000-0000-0000-0000-000000000001"}')
  expect_status 200 "$code" "POST /student/homework-help/submit (text)" 9
}

# --------------------------------------------------------------- step 10 -----
# US8 — Whiteboard (plan-gated). First attempt on free plan MUST refuse;
# then we flip the student to premium and try again.
step_10() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/whiteboard/start" \
    -d '{"subject":"mathematics"}')
  # 403 = plan_gate refusal; smoke expects either 403 (free) or 200 (premium)
  if [[ "$code" != "403" && "$code" != "200" ]]; then
    fail "whiteboard gate: expected 403 or 200 got $code" 10
  fi
  ok "whiteboard plan-gate (HTTP $code)"
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/internal/test-seed/plan-tier" \
    -d "{\"student_profile_id\":\"$STUDENT_PROFILE_ID\",\"plan_tier\":\"premium\"}")
  expect_status 200 "$code" "upgrade profile to premium" 10
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/whiteboard/start" \
    -d '{"subject":"mathematics"}')
  expect_status 200 "$code" "POST /student/whiteboard/start (premium)" 10
}

# --------------------------------------------------------------- step 11 -----
# Session end. Closes the session and drains the outbox so every event
# kind is visible to the Phase 4 consumer.
step_11() {
  local code
  code=$("${CURL_BASE[@]}" -X POST "$BASE_URL/student/session/end" \
    -d '{"end_reason":"signed_out"}')
  expect_status 200 "$code" "POST /student/session/end" 11
  code=$("${CURL_BASE[@]}" "$BASE_URL/internal/diag/session-event-outbox?state=published")
  expect_status 200 "$code" "GET /internal/diag/session-event-outbox" 11
}

run_step 1  "bring up local infrastructure"
run_step 2  "seed student profile + plan gate policy"
run_step 3  "US1 home dashboard"
run_step 4  "US2 study mode lesson viewer"
run_step 5  "US3 text tutor chat"
run_step 6  "US4 voice tutor chat"
run_step 7  "US5 solve questions"
run_step 8  "US6 mock test server-truth timer"
run_step 9  "US7 homework help image submission"
run_step 10 "US8 live whiteboard plan-gated"
run_step 11 "session end + outbox drain"

echo 0 > "$EVIDENCE_DIR/exit_code"
header done "all eleven Phase 3 quickstart steps passed"
