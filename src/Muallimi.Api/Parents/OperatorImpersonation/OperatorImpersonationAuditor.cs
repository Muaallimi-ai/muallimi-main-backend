using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.OperatorImpersonation;

/// <summary>
/// T020 — OperatorImpersonationAuditor.
///
/// Writes a single audit row for every impersonated parent-surface view.
/// Callers invoke <see cref="RecordViewAsync"/> in the same transaction as
/// the response so that an audit row is always paired with an actual render.
/// Missing audit rows on a sampled impersonation run are a readiness-gate
/// failure (see spec CR-001 and FR-021).
///
/// Constitution rule: <see cref="OperatorImpersonationAudit.OperatorActorId"/>
/// is the operator's identity — NEVER the parent identifier.
/// </summary>
public interface IOperatorImpersonationAuditor
{
    Task<Guid> RecordViewAsync(
        Guid tenantId,
        Guid operatorActorId,
        Guid targetParentProfileId,
        Guid? targetChildId,
        string surface,
        string reason,
        string correlationId,
        CancellationToken ct = default);
}

public static class OperatorImpersonationSurfaces
{
    public const string ParentDashboard = "parent_dashboard";
    public const string WeeklyReportViewer = "weekly_report_viewer";
    public const string Preferences = "preferences";
    public const string InterventionPrompt = "intervention_prompt";
    public const string ParentNotifications = "parent_notifications";
}

public sealed class OperatorImpersonationAuditor : IOperatorImpersonationAuditor
{
    private readonly MuallimiDbContext _db;

    public OperatorImpersonationAuditor(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> RecordViewAsync(
        Guid tenantId,
        Guid operatorActorId,
        Guid targetParentProfileId,
        Guid? targetChildId,
        string surface,
        string reason,
        string correlationId,
        CancellationToken ct = default)
    {
        if (operatorActorId == targetParentProfileId)
        {
            throw new InvalidOperationException(
                "operatorActorId MUST NOT equal targetParentProfileId — operator identity cannot be the parent identity.");
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
            TargetParentProfileId = targetParentProfileId,
            TargetChildId = targetChildId,
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

public static class OperatorImpersonationAuditorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4OperatorImpersonationAuditor(this IServiceCollection services)
    {
        services.AddScoped<IOperatorImpersonationAuditor, OperatorImpersonationAuditor>();
        return services;
    }
}
