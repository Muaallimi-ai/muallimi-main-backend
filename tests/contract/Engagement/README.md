# Phase 4 Contract Tests

Each subfolder covers one Phase 4 cross-repo contract:

- `ProgressIngestion/`   → `phase4.progress.ingestion`
- `StudentProgress/`     → `phase4.student.progress`
- `ParentDashboard/`     → `phase4.parent.dashboard`
- `WeeklyReport/`        → `phase4.weekly.report`
- `ParentNotifications/` → `phase4.parent.notifications`
- `AtRisk/`              → `phase4.atrisk.intervention`
- `DownstreamEvents/`    → `phase4.downstream.events`

Contract tests assert envelope shape, required fields, tenant headers, and
correlation ID propagation; they do not exercise business logic.
