using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.SchoolDashboard;

/// <summary>
/// T089 (US4) — <see cref="SchoolAggregateView"/> repository.
///
/// Single read/write path for the pre-aggregated school-level and
/// class-level metric rows that back the school admin dashboard. Every
/// lookup is tenant + school-tenant scoped so cross-school reads are
/// impossible. The row is keyed by
/// <c>(school_tenant_id, scope_type, scope_id, subject_id)</c> per the
/// data-model unique index; <see cref="UpsertAsync"/> honours that by
/// treating a matching key as an update (with idempotency through
/// <c>last_event_id</c>).
/// </summary>
public interface ISchoolAggregateViewRepository
{
    Task<IReadOnlyList<SchoolAggregateView>> ListForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default);

    Task<IReadOnlyList<SchoolAggregateView>> ListForScopeAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid? scopeId,
        CancellationToken ct = default);

    Task<SchoolAggregateView?> FindAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid scopeId,
        Guid? subjectId,
        CancellationToken ct = default);

    Task UpsertAsync(SchoolAggregateView row, Guid lastEventId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class SchoolAggregateViewRepository : ISchoolAggregateViewRepository
{
    private readonly MuallimiDbContext _db;

    public SchoolAggregateViewRepository(MuallimiDbContext db) => _db = db;

    public async Task<IReadOnlyList<SchoolAggregateView>> ListForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default)
        => await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.SchoolTenantId == schoolTenantId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SchoolAggregateView>> ListForScopeAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid? scopeId,
        CancellationToken ct = default)
        => await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v =>
                v.TenantId == tenantId
                && v.SchoolTenantId == schoolTenantId
                && v.ScopeType == scopeType
                && (scopeId == null || v.ScopeId == scopeId))
            .ToListAsync(ct);

    public Task<SchoolAggregateView?> FindAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid scopeId,
        Guid? subjectId,
        CancellationToken ct = default)
        => _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v =>
                v.TenantId == tenantId
                && v.SchoolTenantId == schoolTenantId
                && v.ScopeType == scopeType
                && v.ScopeId == scopeId
                && v.SubjectId == subjectId,
                ct);

    public async Task UpsertAsync(SchoolAggregateView row, Guid lastEventId, CancellationToken ct = default)
    {
        var existing = await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v =>
                v.TenantId == row.TenantId
                && v.SchoolTenantId == row.SchoolTenantId
                && v.ScopeType == row.ScopeType
                && v.ScopeId == row.ScopeId
                && v.SubjectId == row.SubjectId,
                ct);

        if (existing is null)
        {
            if (row.AggregateViewId == Guid.Empty) row.AggregateViewId = Guid.NewGuid();
            row.LastEventId = lastEventId;
            row.LastUpdatedAt = DateTime.UtcNow;
            _db.SchoolAggregateViews.Add(row);
            return;
        }

        // Idempotent replay: same last_event_id — skip.
        if (existing.LastEventId == lastEventId) return;

        existing.Grade = row.Grade;
        existing.ActiveStudentCount = row.ActiveStudentCount;
        existing.AverageMastery = row.AverageMastery;
        existing.AtRiskCount = row.AtRiskCount;
        existing.ActiveStreakCount = row.ActiveStreakCount;
        existing.BadgesAwardedCount = row.BadgesAwardedCount;
        existing.LastEventId = lastEventId;
        existing.LastUpdatedAt = DateTime.UtcNow;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class SchoolAggregateViewRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolAggregateViewRepository(this IServiceCollection services)
    {
        services.AddScoped<ISchoolAggregateViewRepository, SchoolAggregateViewRepository>();
        return services;
    }
}
