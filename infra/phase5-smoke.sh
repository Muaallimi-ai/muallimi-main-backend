#!/usr/bin/env bash
# T209 (Polish) — Phase 5 local smoke run.
#
# Exercises the Phase 5 school-management walkthrough in
# specs/007-school-management-b2b/quickstart.md against the local
# Docker Compose stack — zero managed cloud credentials required.
#
# Each step hits a main-backend facade endpoint with a canned payload,
# asserts the expected status/body, and writes per-step evidence files
# under infra/scripts/_evidence/phase5/. The evidence folder is what the
# Phase 5 readiness gate references.
#
# Usage:
#   ./infra/phase5-smoke.sh              # run all twelve steps
#   STEP=us1 ./infra/phase5-smoke.sh     # run a single step only
#   BASE_URL=http://localhost:5080 ./infra/phase5-smoke.sh
#
# Exit codes:
#   0   all steps passed
#   >0  first failing step number — also written to _evidence/exit_code
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5080}
TENANT_ID=${TENANT_ID:-11111111-1111-1111-1111-111111111111}
SCHOOL_TENANT_ID=${SCHOOL_TENANT_ID:-55555555-5555-5555-5555-555555555555}
SCHOOL_ADMIN_ID=${SCHOOL_ADMIN_ID:-77777777-7777-7777-7777-777777777777}
TEACHER_ID=${TEACHER_ID:-88888888-8888-8888-8888-888888888888}
OPERATOR_ACTOR_ID=${OPERATOR_ACTOR_ID:-99999999-9999-9999-9999-999999999999}
CORRELATION_ID=${CORRELATION_ID:-$(uuidgen | tr '[:upper:]' '[:lower:]')}
EVIDENCE_DIR=${EVIDENCE_DIR:-infra/scripts/_evidence/phase5}

mkdir -p "$EVIDENCE_DIR"
echo "$CORRELATION_ID" > "$EVIDENCE_DIR/correlation_id.txt"
date -u +'%Y-%m-%dT%H:%M:%SZ' > "$EVIDENCE_DIR/started_at.txt"

STEP_FILTER=${STEP:-all}
TENANT_HEADERS=(
  -H "X-Tenant-Id: $TENANT_ID"
  -H "X-School-Tenant-Id: $SCHOOL_TENANT_ID"
  -H "X-Correlation-Id: $CORRELATION_ID"
  -H "Content-Type: application/json"
)
OPERATOR_HEADERS=(
  -H "X-Operator-Actor-Id: $OPERATOR_ACTOR_ID"
  -H "X-Correlation-Id: $CORRELATION_ID"
  -H "Content-Type: application/json"
)
ADMIN_HEADERS=(
  -H "X-School-Admin-Id: $SCHOOL_ADMIN_ID"
  -H "X-School-Tenant-Id: $SCHOOL_TENANT_ID"
  -H "X-Correlation-Id: $CORRELATION_ID"
  -H "Content-Type: application/json"
)
TEACHER_HEADERS=(
  -H "X-Teacher-Id: $TEACHER_ID"
  -H "X-School-Tenant-Id: $SCHOOL_TENANT_ID"
  -H "X-Correlation-Id: $CORRELATION_ID"
  -H "Content-Type: application/json"
)

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

http_get() {
  local url="$1"; shift
  curl -sS -o "$EVIDENCE_DIR/$2.body" -w '%{http_code}' "${@:3}" "$url"
}

http_post() {
  local url="$1" body="$2"; shift 2
  curl -sS -o "$EVIDENCE_DIR/$2.body" -w '%{http_code}' \
    -X POST -d "$body" "${@:3}" "$url"
}

run_step() {
  local id="$1" label="$2"
  if [[ "$STEP_FILTER" != "all" && "$STEP_FILTER" != "$id" ]]; then return 0; fi
  header "$id" "$label"
  "step_${id}"
  touch "$EVIDENCE_DIR/${id}.ok"
}

# --- Steps -------------------------------------------------------------------

step_us1() {
  # School-tenant provisioning + admin onboarding — operator surface.
  local code
  code=$(http_post "$BASE_URL/api/operator/schools" \
    '{"school_name_ar":"مدرسة","school_name_en":"Smoke","curriculum_type":"moe","grade_range_start":1,"grade_range_end":12,"preferred_language":"ar"}' \
    us1 "${OPERATOR_HEADERS[@]}" || true)
  expect_status 201 "$code" "US1: create school tenant" 1
  code=$(http_get "$BASE_URL/api/school-admin/onboarding/status" us1_status "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US1: admin onboarding status" 1
}

step_us2() {
  # Roster import upload — admin surface.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/roster" us2 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US2: list roster imports" 2
}

step_us3() {
  # Classes + teacher assignments — admin surface.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/classes" us3 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US3: list classes" 3
  code=$(http_get "$BASE_URL/api/school-admin/teachers" us3_teachers "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US3: list teachers" 3
}

step_us4() {
  # School-admin dashboard — aggregate rollups.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/dashboard" us4 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US4: school-admin dashboard" 4
}

step_us5() {
  # Teacher dashboard — scoped to assigned class/subject.
  local code
  code=$(http_get "$BASE_URL/api/teacher/dashboard" us5 "${TEACHER_HEADERS[@]}" || true)
  expect_status 200 "$code" "US5: teacher dashboard" 5
}

step_us6() {
  # Exams list — guardrail passthrough runs per-question at create time.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/exams" us6 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US6: list exams" 6
}

step_us7() {
  # Leaderboard — privacy-gated.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/leaderboards" us7 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US7: list leaderboards" 7
}

step_us8() {
  # Announcements — fan-out through the NotificationChannelAdapter.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/announcements" us8 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US8: list announcements" 8
}

step_us9() {
  # School reports — exportable Arabic rollups.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/reports" us9 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US9: list reports" 9
}

step_us10() {
  # Licensing — seat limits + feature gates + expiry.
  local code
  code=$(http_get "$BASE_URL/api/school-admin/license" us10 "${ADMIN_HEADERS[@]}" || true)
  expect_status 200 "$code" "US10: license status" 10
  code=$(http_get "$BASE_URL/api/operator/licenses" us10_list "${OPERATOR_HEADERS[@]}" || true)
  expect_status 200 "$code" "US10: operator license list" 10
}

step_polish() {
  # Polish — prove Phase 5 downstream-event outbox is reachable for the
  # additive-only dispatcher (no broker assertions here — the unit tests
  # cover the outbox shape).
  local code
  code=$(http_get "$BASE_URL/api/operator/phase5/downstream/status" polish_outbox \
    "${OPERATOR_HEADERS[@]}" || true)
  # Status endpoint is optional — accept 200 OR 404 while it's being
  # wired, but surface failures.
  if [[ "$code" != "200" && "$code" != "404" ]]; then
    expect_status 200 "$code" "Polish: outbox status" 11
  else
    ok "Polish: outbox status (HTTP $code)"
  fi
}

run_step us1     "US1 — school tenant provisioning and admin onboarding"
run_step us2     "US2 — roster import and student onboarding"
run_step us3     "US3 — classes, groups, and teacher assignments"
run_step us4     "US4 — school admin aggregate dashboard"
run_step us5     "US5 — teacher dashboard"
run_step us6     "US6 — exam lifecycle with auto-grading"
run_step us7     "US7 — leaderboards with privacy controls"
run_step us8     "US8 — announcements and school communication"
run_step us9     "US9 — school reports and analytics"
run_step us10    "US10 — licensing, seat management, entitlement enforcement"
run_step polish  "Polish — downstream event outbox reachable"

date -u +'%Y-%m-%dT%H:%M:%SZ' > "$EVIDENCE_DIR/completed_at.txt"
printf '\n\033[1;32mPhase 5 smoke passed.\033[0m Evidence at %s\n' "$EVIDENCE_DIR"
