using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.SchoolDashboard;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.Phase4EventConsumer;

/// <summary>
/// T015 + T086 (US4) — <c>SchoolAggregateViewUpdater</c>.
///
/// Converts Phase 4 <see cref="Phase4DownstreamEventEnvelope"/> payloads into
/// <see cref="SchoolAggregateView"/> upserts. Idempotency is enforced via
/// <c>last_event_id</c>: a matching id on any aggregate row for the event's
/// tenant is treated as a replay and skipped. The envelope does not carry
/// the school tenant id; we resolve it by looking up the student's active
/// <see cref="ClassEnrolment"/> rows within the tenant, which pinpoint the
/// owning school(s).
///
/// Actual aggregation is delegated to
/// <see cref="ISchoolDashboardService.RebuildForSchoolAsync"/> so the
/// dashboard read path and the consumer write path share a single, tested
/// derivation from Phase 4 state.
/// </summary>
public interface ISchoolAggregateViewUpdater
{
    Task ApplyAsync(Phase4DownstreamEventEnvelope envelope, CancellationToken ct = default);
}

public sealed class SchoolAggregateViewUpdater : ISchoolAggregateViewUpdater
{
    private readonly MuallimiDbContext _db;
    private readonly ISchoolDashboardService _dashboard;

    public SchoolAggregateViewUpdater(MuallimiDbContext db, ISchoolDashboardService dashboard)
    {
        _db = db;
        _dashboard = dashboard;
    }

    public async Task ApplyAsync(Phase4DownstreamEventEnvelope envelope, CancellationToken ct = default)
    {
        var alreadyApplied = await _db.SchoolAggregateViews
            .AsNoTracking()
            .AnyAsync(v => v.TenantId == envelope.TenantId && v.LastEventId == envelope.EventId, ct);
        if (alreadyApplied) return;

        var schoolIds = await ResolveSchoolTenantsForStudentAsync(envelope.TenantId, envelope.StudentId, ct);
        foreach (var schoolTenantId in schoolIds)
        {
            await _dashboard.RebuildForSchoolAsync(envelope.TenantId, schoolTenantId, envelope.EventId, ct);
        }
    }

    private async Task<IReadOnlyCollection<Guid>> ResolveSchoolTenantsForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct)
    {
        var classIds = await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.StudentId == studentId && e.Status == "active")
            .Select(e => e.ClassGroupId)
            .ToListAsync(ct);
        if (classIds.Count == 0) return Array.Empty<Guid>();

        return await _db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && classIds.Contains(c.ClassGroupId))
            .Select(c => c.SchoolTenantId)
            .Distinct()
            .ToListAsync(ct);
    }
}

public static class SchoolAggregateViewUpdaterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolAggregateViewUpdater(this IServiceCollection services)
    {
        services.AddScoped<ISchoolAggregateViewUpdater, SchoolAggregateViewUpdater>();
        return services;
    }
}
