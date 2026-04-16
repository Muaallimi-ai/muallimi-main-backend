# Muallimi Main Backend

## What This Repo Is
Core business logic backend for the Muaallimi platform. DDD modular monolith handling identity, tenancy, curriculum metadata, student progress, parent dashboards, school management, billing, and notifications. One of 4 product repos.

## Tech Stack
- **Runtime**: .NET 10 LTS
- **Framework**: ASP.NET Core (Web API)
- **ORM**: Entity Framework Core
- **Database**: PostgreSQL
- **Architecture**: DDD modular monolith (bounded contexts as modules, not microservices)

## Project Structure
```
src/
  Muallimi.Api/              # ASP.NET Core Web API (controllers, middleware, startup)
  Muallimi.Domain/           # Domain entities, value objects, domain events, interfaces
  Muallimi.Infrastructure/   # EF Core, PostgreSQL, blob storage, queue, cache adapters
  Muallimi.Application/      # Application services, commands, queries, DTOs
infra/                       # Docker, local dev infrastructure configs
db/
  migrations/                # EF Core migrations
tests/
  Muallimi.Api.Tests/
  Muallimi.Domain.Tests/
  Muallimi.Infrastructure.Tests/
docs/foundation/             # Phase 0 foundation docs
```

## Architecture Rules
- **Modular monolith**: Each bounded context (Identity, Curriculum, Progress, School, Billing, Notification) is a module within the monolith, not a separate service
- **DDD**: Entities, value objects, aggregates, domain events, repositories
- **CQRS-lite**: Separate command/query paths where beneficial, not mandatory everywhere
- **Multi-tenant**: Every query must be tenant-scoped. Cross-tenant access is always denied
- **Audit**: Sensitive actions (identity, role, tenant, content approval, billing) must produce audit events

## Cross-Repo Dependencies
- **Produces APIs for**: muallimi-frontend (all UI data), muallimi-ai-service (curriculum metadata, student context)
- **Consumes from**: muallimi-ai-service (AI request logs, tutor results), muallimi-document-ingestion (ingestion status, published assets via events/queue)
- **Queue messages**: Receives content-published events from document-ingestion; sends notification requests

## Local Dev Dependencies
- PostgreSQL + pgvector (container)
- Local blob-compatible storage (MinIO or Azurite)
- Local queue/broker (RabbitMQ or local alternative)
- Local Redis-compatible cache
- No Azure/Cloudflare credentials required for local dev

## Specs & Contracts
Planning docs location: `../Muaallimi-Platform-Planning-Docs-main/specs/`
- Phase 0: `specs/002-foundation-local-parity/` (identity, tenancy, audit baselines)
- Phase 1: `specs/003-curriculum-content-ingestion/` (curriculum admin API)
- Phase 2: `specs/004-ai-tutor-rag/` (student context for AI)
- Phase 3: `specs/005-student-learning-experience/` (session state, student prefs)
- Phase 4: `specs/006-engagement-progress-parent/` (progress, mastery, parent dashboard API)

## Commands
```bash
dotnet restore           # Restore packages
dotnet build             # Build
dotnet test              # Run tests
dotnet run --project src/Muallimi.Api  # Start API
dotnet ef migrations add <Name> --project src/Muallimi.Infrastructure  # Add migration
```

## Phase Participation
| Phase | Role |
|-------|------|
| Phase 0 - Foundation | **Primary** - identity, tenancy, audit, health, contracts |
| Phase 1 - Content Ingestion | Curriculum metadata API, review queue API |
| Phase 2 - AI Tutor | Student context provider, AI request log storage |
| Phase 3 - Student Experience | Session state, student preferences API |
| Phase 4 - Engagement | **Primary** - progress calculation, parent dashboard API, reports |
| Phase 5 - School Management | **Primary** - school admin, roster, exams, leaderboards |
| Phase 6 - SaaS Operations | Billing, notifications, operational APIs |
