# Repository Charter: Muallimi Main Backend (T035)

**Model**: RepositoryCharter
**Status**: approved
**Last Updated**: 2026-04-16

---

## repository_id

`main-backend`

---

## display_name

Muallimi Main Backend

---

## primary_owner

Backend Engineering Team

---

## responsibilities

### Core Platform

| Responsibility Area | Description |
|--------------------|-------------|
| **Identity** | User authentication (username/password, OAuth flows), JWT issuance, token refresh and revocation, password reset, MFA scaffolding |
| **Tenancy** | Multi-tenant data isolation enforcement; family, school, and platform tenant lifecycle; tenant configuration and tier management |
| **Roles and Permissions** | Role assignment (student, parent, school-admin, teacher, curriculum-admin, subject-expert, platform-operator); permission resolution per tenant context; role change auditing |
| **Audit** | Audit event logging for all identity, role, tenant, content approval, billing, provider configuration, and security-relevant actions; audit record storage and query API |

### Academic and Curriculum

| Responsibility Area | Description |
|--------------------|-------------|
| **Curriculum Metadata** | Subject, grade, learning-objective, unit, and topic registry; curriculum structure API; metadata versioning; linkage from curriculum entries to content items produced by `document-ingestion` |
| **Student Progress** | Progress record storage and calculation; mastery state transitions; streak tracking; badge award logic; progress history retention |
| **Assignment Tracking** | Assignment lifecycle (assigned, in-progress, submitted, graded); grade storage; teacher feedback records |

### Dashboards and Reporting

| Responsibility Area | Description |
|--------------------|-------------|
| **Parent Dashboards** | Progress summary API for parent view; child linkage; report generation (PDF-style summary); child session history for parental review |
| **School Analytics (Phase 4+)** | Aggregate class and school progress reports; teacher and school-admin query APIs; cohort comparison data |
| **Admin Portal Data** | Platform operator APIs for tenant management, user management, billing oversight, and content approval status |

### Operations

| Responsibility Area | Description |
|--------------------|-------------|
| **School Management** | School tenant creation, school admin assignment, grade/subject access configuration per school, teacher roster management |
| **Billing** | Subscription record management; plan tier enforcement; billing event emission; integration with payment provider adapter (Phase 6) |
| **Notifications** | Notification dispatch (in-app, email scaffolding); notification preference management; notification event emission to frontend contract |
| **Observability** | Health and readiness endpoint; structured logging; distributed trace participation as producer of correlation fields |

### Cross-Repository Contract Producer

`main-backend` is the **authoritative producer** of the following contracts consumed by other repositories:

- `identity-claim-contract` — consumed by `frontend`, `ai-service`, `document-ingestion`
- `tenant-context-contract` — consumed by `frontend`, `ai-service`, `document-ingestion`
- `audit-event-contract` — consumed by all repos (they emit events; `main-backend` defines the schema)
- `student-progress-summary-contract` — consumed by `frontend`
- `notification-created-event` — consumed by `frontend`
- `badge-awarded-event` — consumed by `frontend`
- `subscription-status-contract` — consumed by `frontend`, `ai-service`

---

## release_boundary

### Independent Release Capability

`main-backend` may be released independently of all other repositories provided:

1. All contracts it **produces** remain backward compatible with current consumers at their deployed versions, **or** a consumer migration window has been formally agreed in the contract catalogue
2. All contracts it **consumes** (from `ai-service` and `document-ingestion`) are not broken by the release
3. The health endpoint returns healthy before the release is marked complete
4. Database migrations complete successfully with no locked tables beyond the migration window
5. No audit gap is introduced: audit event records before and after release remain consistent

### Release Triggers

| Trigger | Action |
|---------|--------|
| Feature complete for a sprint | Tag release candidate; run readiness gates |
| Hotfix for production defect | Emergency release; notify consumer teams within 1 hour |
| Schema migration | Coordinate with consumer teams if migration affects a contract field |
| Breaking contract change | Requires cross-repo review approval before release (see `cross-repo-review-checklist.md`) |

### What Requires Coordination

| Scenario | Required Coordination |
|----------|----------------------|
| Identity claim field added (additive) | Notify consumer leads; no blocking coordination needed |
| Identity claim field removed or renamed | Full cross-repo review; all consumers must acknowledge migration |
| Tenant context shape changed | Full cross-repo review |
| Audit event schema changed | Notify all repo leads; confirm additive-only |
| New notification event type | Notify `frontend` lead; confirm frontend can handle unknown event types gracefully |

---

## reviewers_required

### Normal PR (within `main-backend`)

| Reviewer | Condition |
|----------|-----------|
| Backend team lead | Always; minimum 1 approval required |
| Backend peer reviewer | Recommended for all changes; required for schema migrations |

### Cross-Repository Change (contract-affecting PR)

| Reviewer | Condition |
|----------|-----------|
| Backend team lead (owner) | Always required |
| Frontend lead | Required when identity, progress, notification, or parent dashboard contract changes |
| AI service lead | Required when identity claim, tenant context, or subscription contract changes |
| Document ingestion lead | Required when identity claim or tenant context contract changes |
| Security reviewer | Required for all identity, tenancy, billing, and audit changes |
| Contract governance lead | Required when contract version is bumped or a breaking change is declared |

### Special Review Rules

- Any change to authentication flow or JWT issuance requires security reviewer approval
- Any change to tenant isolation logic requires security reviewer approval
- Any change to billing record logic requires security reviewer approval
- Any change to audit event schema requires all repo leads to be notified even if review is not formally blocking

---

## phase_participation

| Phase | Role | Description |
|-------|------|-------------|
| **Phase 0 - Foundation** | Primary (foundation author) | Identity and JWT baseline; tenant and role baseline; audit event baseline; health/readiness endpoint; database schema foundation; cross-repo contract producer for identity and tenancy |
| **Phase 1 - Content Ingestion** | Consumer + Metadata Owner | Curriculum metadata registry; tracks upload and content approval status from `document-ingestion` events; exposes content-status API to `frontend` |
| **Phase 2 - AI Tutor** | Consumer + Integration Owner | Validates AI tutor interactions against subscription tier; consumes interaction-log events from `ai-service`; stores session and interaction history |
| **Phase 3 - Student Experience** | Primary | Student progress storage and calculation; mastery state; streaks; badges; assignment tracking; progress summary API for `frontend` |
| **Phase 4 - Engagement** | Primary | Engagement analytics; parent progress reports; cohort data; notification dispatch; badge award logic; streak milestones |
| **Phase 5 - School Management** | Primary | School tenant lifecycle; teacher and student roster; grade/subject access configuration; school analytics APIs |
| **Phase 6 - SaaS Operations** | Primary | Billing and subscription management; plan tier enforcement; platform operator management APIs; SaaS metrics; data export |

---

## status

`approved`

---

## Change History

| Date | Change | Author |
|------|--------|--------|
| 2026-04-16 | Initial approved charter created | T035 / US2 |
