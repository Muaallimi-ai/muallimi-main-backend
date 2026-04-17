-- T024 - Phase 3 PlanGatePolicy defaults
--
-- Seeds the global-default (tenant_id IS NULL) policies the Phase 3 plan
-- gate resolver (T011) enforces on every student mode transition. Tenant
-- overrides are added by tenant admins per the Phase 5 admin surface and
-- are NOT seeded here.
--
-- Rules captured here:
--   * home / study / tutor_chat / tutor_voice / solve_questions /
--     mock_test / homework_help: enabled for free, standard, premium.
--   * whiteboard: enabled for premium only, and gated to
--     Mathematics + Physics at MVP.
--
-- Idempotent: rows use deterministic UUIDs keyed to (mode, policy_source)
-- so re-running the seed is safe.

INSERT INTO plan_gate_policies (
    id, tenant_id, mode, required_plan_tiers, subject_scope, grade_scope,
    enabled_at, expires_at, policy_source
) VALUES
  ('00000000-0000-0000-0000-000000000001', NULL, 'home',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  ('00000000-0000-0000-0000-000000000002', NULL, 'study',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  ('00000000-0000-0000-0000-000000000003', NULL, 'tutor_chat',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  ('00000000-0000-0000-0000-000000000004', NULL, 'tutor_voice',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  ('00000000-0000-0000-0000-000000000005', NULL, 'solve_questions',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  ('00000000-0000-0000-0000-000000000006', NULL, 'mock_test',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  ('00000000-0000-0000-0000-000000000007', NULL, 'homework_help',
   '["free","standard","premium"]'::jsonb, NULL, NULL,
   now(), NULL, 'global_default'),
  -- Whiteboard: premium only + Mathematics/Physics only at MVP.
  -- The subject_scope UUIDs are tenant-agnostic subject identifiers that
  -- the Phase 1 curriculum catalogue publishes; tenant seed pipelines can
  -- override this row when the catalogue uses different ids.
  ('00000000-0000-0000-0000-000000000008', NULL, 'whiteboard',
   '["premium"]'::jsonb,
   '["mathematics","physics"]'::jsonb, NULL,
   now(), NULL, 'global_default')
ON CONFLICT (id) DO NOTHING;
