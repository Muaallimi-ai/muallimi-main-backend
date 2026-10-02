#!/usr/bin/env bash
# Stage 8 — Phase 1 curriculum content ingestion local smoke run.
#
# Exercises the Phase 1 (Curriculum Content Ingestion) walkthrough against
# the local Docker Compose stack — zero cloud credentials required for the
# schema / event / dispatcher steps. The teachable-approval step (step 4)
# additionally calls Claude for enrichment and, if Embedding:Provider is
# set, calls the embedding provider — those two rely on the Anthropic +
# Voyage/OpenAI keys already present in ~/.muaallimi.secrets. When those
# keys are absent the smoke degrades cleanly: the Claude call errors are
# logged by the backend, the approval + event steps still succeed.
#
# Each step hits main-backend + postgres directly, asserts the expected
# status/body/row, and writes per-step evidence files under
# infra/scripts/_evidence/phase1/. The evidence folder is what the Phase 1
# readiness gate references.
#
# Usage:
#   ./infra/phase1-smoke.sh              # run all steps
#   STEP=4 ./infra/phase1-smoke.sh       # run a single step only
#   BASE_URL=http://localhost:5063 ./infra/phase1-smoke.sh
#
# Exit codes:
#   0   all steps passed
#   >0  first failing step number — also written to _evidence/exit_code
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5063}
PG_CONTAINER=${PG_CONTAINER:-muallimi-postgres}
PG_USER=${PG_USER:-muallimi}
PG_DB=${PG_DB:-muallimi_dev}
CORRELATION_ID=${CORRELATION_ID:-$(uuidgen | tr '[:upper:]' '[:lower:]')}
EVIDENCE_DIR=${EVIDENCE_DIR:-infra/scripts/_evidence/phase1}
STEP_FILTER=${STEP:-all}

mkdir -p "$EVIDENCE_DIR"
echo "$CORRELATION_ID" > "$EVIDENCE_DIR/correlation_id.txt"
date -u +'%Y-%m-%dT%H:%M:%SZ' > "$EVIDENCE_DIR/started_at.txt"

CURL_JSON=(curl -s -o /dev/null -w '%{http_code}' -H "X-Correlation-Id: $CORRELATION_ID" -H "Content-Type: application/json")
CURL_BODY=(curl -s -H "X-Correlation-Id: $CORRELATION_ID" -H "Content-Type: application/json")

header() { printf '\n\033[1;34m[step %s]\033[0m %s\n' "$1" "$2"; }
ok()     { printf '  \033[1;32m✓\033[0m %s\n' "$1"; }
fail()   { printf '  \033[1;31m✗\033[0m %s\n' "$1"; echo "$2" > "$EVIDENCE_DIR/exit_code"; date -u +'%Y-%m-%dT%H:%M:%SZ' > "$EVIDENCE_DIR/completed_at.txt"; exit "$2"; }

expect_status() {
  local want="$1" got="$2" label="$3" step="$4"
  if [[ "$want" != "$got" ]]; then
    echo "got=$got want=$want label=$label" > "$EVIDENCE_DIR/${step}.fail"
    fail "$label: expected $want got $got" "$step"
  fi
  ok "$label (HTTP $got)"
}

psql_scalar() {
  # $1 = SQL that returns exactly one scalar (single row, single column).
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -tA -c "$1" 2>/dev/null | tr -d '[:space:]'
}

psql_dump() {
  # $1 = SQL, $2 = evidence-file name
  docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -c "$1" > "$EVIDENCE_DIR/$2" 2>&1
}

run_step() {
  local id="$1" label="$2"
  if [[ "$STEP_FILTER" != "all" && "$STEP_FILTER" != "$id" ]]; then return 0; fi
  header "$id" "$label"
  "step_${id}"
  touch "$EVIDENCE_DIR/${id}.ok"
}

# ---------------------------------------------------------------- step 1 -----
# Bring-up. main-backend + postgres reachable, pgvector extension loaded,
# phase1 schema (curriculum_node_embeddings + phase1_downstream_events) present.
step_1() {
  local code
  code=$("${CURL_JSON[@]}" "$BASE_URL/health")
  expect_status 200 "$code" "main-backend healthy" 1

  local ext
  ext=$(psql_scalar "SELECT extname FROM pg_extension WHERE extname='vector';")
  [[ "$ext" == "vector" ]] || fail "pgvector extension not installed" 1
  ok "pgvector extension present"

  # Required columns, not an exact count: the schema evolves additively,
  # so a new nullable column must not fail the readiness gate.
  assert_columns() {
    local table="$1"; shift
    local col missing=""
    for col in "$@"; do
      local found
      found=$(psql_scalar "
        SELECT 1 FROM information_schema.columns
        WHERE table_name='$table' AND column_name='$col';")
      [[ "$found" == "1" ]] || missing="$missing $col"
    done
    [[ -z "$missing" ]] || fail "$table missing required column(s):$missing" 1
    ok "$table has all $# required columns"
  }

  assert_columns curriculum_node_embeddings \
    node_id source_id subject language retrieval_class embed_body_sha256 \
    provider_key model_name dim voyage_embedding openai_embedding \
    local_embedding embedded_at

  assert_columns phase1_downstream_events \
    phase1_downstream_event_id event_kind payload correlation_id \
    occurred_at dispatched_at delivery_state dispatch_attempts

  psql_dump "\d+ curriculum_node_embeddings" 1.embeddings_schema.txt
  psql_dump "\d+ phase1_downstream_events" 1.outbox_schema.txt
}

# ---------------------------------------------------------------- step 2 -----
# Fixture check. At least one curriculum_source exists with a persisted
# structure. The smoke does not upload a fresh PDF — that would burn
# Anthropic credit on every re-run; instead it exercises the approval flow
# against a source already extracted in this environment.
step_2() {
  local src_count
  src_count=$(psql_scalar "SELECT COUNT(*) FROM curriculum_sources WHERE status IN ('Extracted','InReview','Approved');")
  [[ "$src_count" -ge 1 ]] || fail "No curriculum_source ready for approval flow (upload + extract one first)" 2
  ok "$src_count source(s) available"

  # Pick the source id that has the most Ready content rows — the one most
  # useful for exercising approval.
  SOURCE_ID=$(psql_scalar "SELECT source_id::text FROM curriculum_node_contents WHERE status='Ready' GROUP BY source_id ORDER BY COUNT(*) DESC LIMIT 1;")
  [[ -n "$SOURCE_ID" ]] || fail "No source with Ready content rows" 2
  echo "$SOURCE_ID" > "$EVIDENCE_DIR/2.source_id.txt"
  ok "picked source $SOURCE_ID"

  local ready_count
  ready_count=$(psql_scalar "SELECT COUNT(*) FROM curriculum_node_contents WHERE source_id='$SOURCE_ID' AND status='Ready';")
  ok "$ready_count Ready content row(s) on source $SOURCE_ID"
}

# ---------------------------------------------------------------- step 3 -----
# Retrieval-class hydration. Backfill endpoint stamps system_retrieval_class
# on every node so downstream approval decisions have a class to react to.
# Idempotent — safe to call every smoke run.
step_3() {
  SOURCE_ID=$(cat "$EVIDENCE_DIR/2.source_id.txt")
  local code
  code=$("${CURL_JSON[@]}" -X POST "$BASE_URL/admin/curriculum/sources/$SOURCE_ID/backfill-retrieval-classes")
  expect_status 200 "$code" "backfill retrieval classes" 3

  local class_count
  class_count=$(psql_scalar "SELECT COUNT(*) FROM (
    WITH RECURSIVE walk(n) AS (
      SELECT jsonb_array_elements(nodes) FROM curriculum_structures WHERE source_id='$SOURCE_ID'
      UNION ALL
      SELECT jsonb_array_elements(n->'children') FROM walk WHERE jsonb_typeof(n->'children')='array'
    )
    SELECT n FROM walk WHERE n ? 'system_retrieval_class'
  ) t;")
  [[ "$class_count" -ge 1 ]] || fail "backfill did not stamp system_retrieval_class on any node" 3
  ok "$class_count nodes now carry system_retrieval_class"
}

# ---------------------------------------------------------------- step 4 -----
# Approve one teachable node. If none unapproved remain, the smoke picks
# an already-approved teachable node and re-approves it (idempotent on
# CurriculumNodeContent.Approve when already-approved). The approve
# endpoint runs: enrich → embed → publish event. Enrichment or embed
# failures don't block the event.
step_4() {
  SOURCE_ID=$(cat "$EVIDENCE_DIR/2.source_id.txt")
  TEACHABLE_NODE=$(psql_scalar "
    WITH RECURSIVE walk(n) AS (
      SELECT jsonb_array_elements(nodes) FROM curriculum_structures WHERE source_id='$SOURCE_ID'
      UNION ALL
      SELECT jsonb_array_elements(n->'children') FROM walk WHERE jsonb_typeof(n->'children')='array'
    )
    SELECT c.node_id::text
    FROM walk, curriculum_node_contents c
    WHERE c.node_id::text = (n->>'node_id')
      AND c.source_id='$SOURCE_ID'
      AND c.status='Ready'
      AND (n->>'system_retrieval_class')='teachable'
    ORDER BY c.is_approved ASC, c.requested_at DESC
    LIMIT 1;")
  [[ -n "$TEACHABLE_NODE" ]] || fail "no teachable Ready node on source $SOURCE_ID" 4
  echo "$TEACHABLE_NODE" > "$EVIDENCE_DIR/4.teachable_node.txt"

  "${CURL_BODY[@]}" -X POST "$BASE_URL/admin/curriculum/nodes/$TEACHABLE_NODE/content/approve" \
    > "$EVIDENCE_DIR/4.response.json"
  ok "approve returned"

  local retrieval_class
  retrieval_class=$(python3 -c "import json,sys; print(json.load(sys.stdin).get('retrieval_class',''))" < "$EVIDENCE_DIR/4.response.json")
  [[ "$retrieval_class" == "teachable" ]] || fail "expected retrieval_class=teachable got $retrieval_class" 4
  ok "response.retrieval_class = teachable"

  local sha
  sha=$(python3 -c "import json,sys; print(json.load(sys.stdin).get('embed_body_sha256','') or '')" < "$EVIDENCE_DIR/4.response.json")
  [[ -n "$sha" ]] || fail "no embed_body_sha256 on response (embed body composition failed)" 4
  ok "embed_body_sha256 = ${sha:0:16}..."

  # When a real provider ran (local sidecar, voyage or openai), the
  # pgvector row MUST have landed on that provider's column. With
  # Embedding:Provider=null there is nothing to assert.
  local provider column row_count
  provider=$(python3 -c "import json,sys; print(json.load(sys.stdin).get('embed_provider','') or '')" < "$EVIDENCE_DIR/4.response.json")
  if [[ -z "$provider" || "$provider" == "null" ]]; then
    ok "embed skipped (provider=null) — pgvector row not asserted"
    return 0
  fi

  case "$provider" in
    local)  column="local_embedding" ;;
    voyage) column="voyage_embedding" ;;
    openai) column="openai_embedding" ;;
    *)      fail "unknown embed_provider '$provider' — no column mapping" 4 ;;
  esac

  row_count=$(psql_scalar "
    SELECT count(*) FROM curriculum_node_embeddings
    WHERE node_id='$TEACHABLE_NODE'
      AND provider_key='$provider'
      AND $column IS NOT NULL
      AND embed_body_sha256='$sha';")
  [[ "$row_count" == "1" ]] || fail "expected 1 pgvector row for $provider/$column, got $row_count" 4
  ok "pgvector row present: provider=$provider column=$column"

  # psql_scalar strips whitespace, so keep the summary space-free.
  psql_scalar "
    SELECT 'provider=' || provider_key || ',dim=' || dim || ',model=' || model_name
    FROM curriculum_node_embeddings WHERE node_id='$TEACHABLE_NODE';" \
    > "$EVIDENCE_DIR/4.embedding_row.txt"
  ok "embedding row: $(cat "$EVIDENCE_DIR/4.embedding_row.txt")"
}

# ---------------------------------------------------------------- step 5 -----
# Approve one non-teachable node. Event should still fire but all embed_*
# fields should be null. Skipped if the source has no non-teachable Ready
# nodes.
step_5() {
  SOURCE_ID=$(cat "$EVIDENCE_DIR/2.source_id.txt")
  NONTEACH_NODE=$(psql_scalar "
    WITH RECURSIVE walk(n) AS (
      SELECT jsonb_array_elements(nodes) FROM curriculum_structures WHERE source_id='$SOURCE_ID'
      UNION ALL
      SELECT jsonb_array_elements(n->'children') FROM walk WHERE jsonb_typeof(n->'children')='array'
    )
    SELECT c.node_id::text
    FROM walk, curriculum_node_contents c
    WHERE c.node_id::text = (n->>'node_id')
      AND c.source_id='$SOURCE_ID'
      AND c.status='Ready'
      AND COALESCE((n->>'system_retrieval_class'),'structural') <> 'teachable'
    ORDER BY c.is_approved ASC, c.requested_at DESC
    LIMIT 1;")
  if [[ -z "$NONTEACH_NODE" ]]; then
    ok "no non-teachable Ready node on source (skip)"
    return 0
  fi
  echo "$NONTEACH_NODE" > "$EVIDENCE_DIR/5.nonteachable_node.txt"

  "${CURL_BODY[@]}" -X POST "$BASE_URL/admin/curriculum/nodes/$NONTEACH_NODE/content/approve" \
    > "$EVIDENCE_DIR/5.response.json"

  local embedded
  embedded=$(python3 -c "import json,sys; print(str(json.load(sys.stdin).get('embedded',True)).lower())" < "$EVIDENCE_DIR/5.response.json")
  [[ "$embedded" == "false" ]] || fail "non-teachable approve returned embedded=$embedded (expected false)" 5
  ok "non-teachable approve: embedded=false ✓"
}

# ---------------------------------------------------------------- step 6 -----
# Outbox row for the teachable approval landed and was dispatched.
# The dispatcher polls every 1s so we allow up to 10s.
step_6() {
  TEACHABLE_NODE=$(cat "$EVIDENCE_DIR/4.teachable_node.txt")
  local state=""
  for i in $(seq 1 10); do
    state=$(psql_scalar "SELECT delivery_state FROM phase1_downstream_events WHERE payload->>'node_id'='$TEACHABLE_NODE' ORDER BY occurred_at DESC LIMIT 1;")
    [[ "$state" == "dispatched" ]] && break
    sleep 1
  done
  [[ "$state" == "dispatched" ]] || fail "outbox row for $TEACHABLE_NODE not dispatched (state=$state)" 6
  ok "outbox row dispatched"

  psql_dump "SELECT event_kind, delivery_state, dispatch_attempts, correlation_id, jsonb_pretty(payload) FROM phase1_downstream_events WHERE payload->>'node_id'='$TEACHABLE_NODE' ORDER BY occurred_at DESC LIMIT 1;" 6.outbox_row.txt

  local kind
  kind=$(psql_scalar "SELECT event_kind FROM phase1_downstream_events WHERE payload->>'node_id'='$TEACHABLE_NODE' ORDER BY occurred_at DESC LIMIT 1;")
  [[ "$kind" == "curriculum.node.approved" ]] || fail "event_kind=$kind (expected curriculum.node.approved)" 6
  ok "wire event_kind = curriculum.node.approved"
}

# ---------------------------------------------------------------- step 7 -----
# Prompt registry stamping — Stage 4's promise. A source extracted after
# Stage 4 shipped will have prompt_key/version/sha stamped; legacy
# sources may have nulls (that's fine, the columns exist and are wired).
step_7() {
  local col_count
  col_count=$(psql_scalar "SELECT COUNT(*) FROM information_schema.columns WHERE table_name='curriculum_sources' AND column_name IN ('prompt_key','prompt_version','prompt_sha');")
  [[ "$col_count" == "3" ]] || fail "curriculum_sources missing prompt_ columns (got $col_count/3)" 7
  ok "curriculum_sources has prompt_key/version/sha columns"

  local stamped
  stamped=$(psql_scalar "SELECT COUNT(*) FROM curriculum_sources WHERE prompt_key IS NOT NULL;")
  ok "$stamped source(s) have prompt metadata stamped"
}

# ---------------------------------------------------------------- step 8 -----
# Contract handoff: the additive-only contract test must pass. Uses the
# already-built test dll so the smoke stays fast (no rebuild).
step_8() {
  cd "$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
  dotnet test tests/Muallimi.Api.Tests/Muallimi.Api.Tests.csproj \
    --nologo --no-build \
    --filter "FullyQualifiedName~CurriculumContent.DownstreamEvents" \
    > "$EVIDENCE_DIR/8.contract_test.txt" 2>&1 || fail "contract test failed" 8
  ok "contract test passed (see 8.contract_test.txt)"
}

run_step 1 "Bring-up + schema check"
run_step 2 "Fixture check + pick source"
run_step 3 "Backfill retrieval classes"
run_step 4 "Approve teachable node → enrich → embed → event"
run_step 5 "Approve non-teachable node → event only"
run_step 6 "Verify outbox → dispatched"
run_step 7 "Prompt registry stamps present"
run_step 8 "Additive-only contract test"

date -u +'%Y-%m-%dT%H:%M:%SZ' > "$EVIDENCE_DIR/completed_at.txt"
echo 0 > "$EVIDENCE_DIR/exit_code"

printf '\n\033[1;32mphase1-smoke: all steps passed.\033[0m\n'
printf 'Evidence written to %s\n' "$EVIDENCE_DIR"
