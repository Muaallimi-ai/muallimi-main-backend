using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

/// <summary>
/// T021 — <c>SchoolOperatorImpersonationAuditor</c>.
///
/// Reuses the Phase 4 <see cref="OperatorImpersonationAudit"/> row shape
/// extended with school-specific surface identifiers. Writes a single audit
/// row for every impersonated school-admin or teacher surface view.
///
/// Callers invoke <see cref="RecordViewAsync"/> in the same transaction as
/// the response so an audit row is always paired with an actual render.
/// Missing audit rows on a sampled impersonation run are a readiness-gate
/// failure (FR-021 and CR-001).
///
/// Constitution rule: <see cref="OperatorImpersonationAudit.OperatorActorId"/>
/// is the operator's identity — NEVER the impersonated school admin or
/// teacher identifier.
/// </summary>
public interface ISchoolOperatorImpersonationAuditor
{
    Task<Guid> RecordViewAsync(
        Guid tenantId,
        Guid operatorActorId,
        Guid schoolTenantId,
        Guid? targetUserIdentityId,
        string surface,
        string reason,
        string correlationId,
        CancellationToken ct = default);
}

public static class SchoolOperatorImpersonationSurfaces
{
    public const string SchoolAdminDashboard = "school_admin_dashboard";
    public const string SchoolAdminRoster = "school_admin_roster";
    public const string SchoolAdminClasses = "school_admin_classes";
    public const string SchoolAdminExams = "school_admin_exams";
    public const string SchoolAdminAnnouncements = "school_admin_announcements";
    public const string SchoolAdminReports = "school_admin_reports";
    public const string SchoolAdminLicensing = "school_admin_licensing";
    public const string TeacherDashboard = "teacher_dashboard";
}

public sealed class SchoolOperatorImpersonationAuditor : ISchoolOperatorImpersonationAuditor
{
    private readonly MuallimiDbContext _db;

    public SchoolOperatorImpersonationAuditor(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> RecordViewAsync(
        Guid tenantId,
        Guid operatorActorId,
        Guid schoolTenantId,
        Guid? targetUserIdentityId,
        string surface,
        string reason,
        string correlationId,
        CancellationToken ct = default)
    {
        if (targetUserIdentityId is not null && operatorActorId == targetUserIdentityId)
        {
            throw new InvalidOperationException(
                "operatorActorId MUST NOT equal targetUserIdentityId — operator identity cannot be the impersonated subject.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason is required on every impersonation audit row.", nameof(reason));
        }

        var row = new OperatorImpersonationAudit
        {
            OperatorImpersonationAuditId = Guid.NewGuid(),
            TenantId = tenantId,
            OperatorActorId = operatorActorId,
            // Phase 4 row shape stores the parent/impersonation-target id; for
            // school-admin and teacher impersonation we overload
            // TargetParentProfileId with the school_tenant_id and
            // TargetChildId with the impersonated user_identity_id so the
            // Phase 4 retention + audit export pipeline continues to work.
            TargetParentProfileId = schoolTenantId,
            TargetChildId = targetUserIdentityId,
            Surface = surface,
            Reason = reason,
            CorrelationId = correlationId,
            ViewedAt = DateTime.UtcNow,
        };
        _db.OperatorImpersonationAudits.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.OperatorImpersonationAuditId;
    }
}

public static class SchoolOperatorImpersonationAuditorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolOperatorImpersonationAuditor(this IServiceCollection services)
    {
        services.AddScoped<ISchoolOperatorImpersonationAuditor, SchoolOperatorImpersonationAuditor>();
        return services;
    }
}
