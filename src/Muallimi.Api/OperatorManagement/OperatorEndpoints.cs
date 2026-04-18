using Microsoft.EntityFrameworkCore;
using Muallimi.Api.AiOperations;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.OperatorManagement.FeatureFlags;
using Muallimi.Api.OperatorManagement.Impersonation;
using Muallimi.Api.OperatorManagement.TenantHealth;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.OperatorManagement;

/// <summary>
/// T100–T102, T108 — Operator Platform Management endpoints per
/// operator-management-contract.md. All endpoints are operator-gated via
/// <c>X-Actor-Role: operator</c>. Feature flag toggles, impersonation start/end,
/// and tenant refresh emit AuditEntry rows through <see cref="AuditTrailWriter"/>.
/// </summary>
public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorManagementEndpoints(this IEndpointRouteBuilder routes)
    {
        // ── T100: Tenant Health ────────────────────────────────────────────
        routes.MapGet("/api/v1/operator/tenants", ListTenantsAsync);
        routes.MapGet("/api/v1/operator/tenants/{tenantId:guid}", GetTenantAsync);

        // ── T101: Feature Flags ────────────────────────────────────────────
        routes.MapGet("/api/v1/operator/tenants/{tenantId:guid}/feature-flags", ListFlagsAsync);
        routes.MapPut("/api/v1/operator/tenants/{tenantId:guid}/feature-flags/{flagName}", SetFlagAsync);

        // ── T102: Impersonation ────────────────────────────────────────────
        routes.MapPost("/api/v1/operator/impersonate", StartImpersonationAsync);
        routes.MapPost("/api/v1/operator/impersonate/end", EndImpersonationAsync);

        return routes;
    }

    // ── T100 handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> ListTenantsAsync(
        HttpContext http,
        MuallimiDbContext db,
        string? search,
        string? tenant_type,
        string? subscription_status,
        string? sort_by,
        string? sort_direction,
        int? limit,
        string? cursor,
        string? locale,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var loc = locale == "en" ? "en" : "ar";
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        var query = db.TenantHealthViews.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(tenant_type))
        {
            query = query.Where(v => v.TenantType == tenant_type);
        }
        if (!string.IsNullOrWhiteSpace(subscription_status))
        {
            query = query.Where(v => v.SubscriptionStatus == subscription_status);
        }

        var rows = await query.ToListAsync(ct);
        var schools = await db.SchoolTenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(s => new { s.TenantId, s.SchoolNameAr, s.SchoolNameEn })
            .ToListAsync(ct);
        var nameByTenant = schools.ToDictionary(
            s => s.TenantId,
            s => loc == "ar" ? s.SchoolNameAr : s.SchoolNameEn);

        var enriched = rows.Select(v => new
        {
            v,
            name = nameByTenant.TryGetValue(v.TenantId, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : (loc == "ar" ? "حساب عائلة" : "Family Tenant"),
        }).ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            enriched = enriched
                .Where(x => x.name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || x.v.TenantId.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var desc = string.Equals(sort_direction, "desc", StringComparison.OrdinalIgnoreCase);
        enriched = (sort_by switch
        {
            "subscription_status" => desc
                ? enriched.OrderByDescending(x => x.v.SubscriptionStatus).ToList()
                : enriched.OrderBy(x => x.v.SubscriptionStatus).ToList(),
            "active_students" => desc
                ? enriched.OrderByDescending(x => x.v.ActiveStudentCount).ToList()
                : enriched.OrderBy(x => x.v.ActiveStudentCount).ToList(),
            "ai_cost" => desc
                ? enriched.OrderByDescending(x => x.v.MonthlyAiCostEgp).ToList()
                : enriched.OrderBy(x => x.v.MonthlyAiCostEgp).ToList(),
            "last_activity" => desc
                ? enriched.OrderByDescending(x => x.v.LastActivityAt ?? DateTime.MinValue).ToList()
                : enriched.OrderBy(x => x.v.LastActivityAt ?? DateTime.MinValue).ToList(),
            _ => desc
                ? enriched.OrderByDescending(x => x.name).ToList()
                : enriched.OrderBy(x => x.name).ToList(),
        });

        var totalCount = enriched.Count;
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0)
        {
            offset = parsed;
        }

        var page = enriched.Skip(offset).Take(pageSize).ToList();
        var next = offset + pageSize < totalCount ? (offset + pageSize).ToString() : null;

        var tenants = page.Select(x => new
        {
            tenant_id = x.v.TenantId,
            tenant_name = x.name,
            tenant_type = x.v.TenantType,
            subscription_status = x.v.SubscriptionStatus,
            plan_tier = x.v.PlanTier,
            active_student_count = x.v.ActiveStudentCount,
            monthly_session_count = x.v.MonthlySessionCount,
            monthly_ai_cost_egp = x.v.MonthlyAiCostEgp,
            engagement_score = x.v.EngagementScore,
            at_risk_student_count = x.v.AtRiskStudentCount,
            last_activity_at = x.v.LastActivityAt,
        });

        return Results.Ok(new { tenants, total_count = totalCount, next_cursor = next });
    }

    private static async Task<IResult> GetTenantAsync(
        HttpContext http,
        MuallimiDbContext db,
        TenantHealthRollupService rollup,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;

        var view = await db.TenantHealthViews
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);
        if (view is null)
        {
            view = await rollup.RefreshAsync(tenantId, ct);
        }

        var school = await db.SchoolTenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        var sub = await db.Subscriptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        SubscriptionPlan? plan = null;
        if (sub is not null)
        {
            plan = await db.SubscriptionPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlanId == sub.PlanId, ct);
        }

        var paidTotal = await db.PaymentTransactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Status == "success" && p.TransactionType == "charge")
            .SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var lastPayment = await db.PaymentTransactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Status == "success")
            .MaxAsync(p => (DateTime?)p.CompletedAt, ct);
        var failedPayments = await db.PaymentTransactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(p => p.TenantId == tenantId && p.Status == "failed", ct);

        var monthStart = DateTime.UtcNow.AddDays(-30);
        var aiRequests30d = await db.Phase6AIOperationsMetrics
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(m => m.TenantId == tenantId && m.OccurredAt >= monthStart, ct);

        object? schoolDetails = null;
        if (school is not null)
        {
            var classCount = await db.ClassGroups
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(c => c.TenantId == tenantId && c.IsActive, ct);
            var teacherCount = await db.Teachers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(t => t.TenantId == tenantId && t.DeactivatedAt == null, ct);
            var licence = await db.SchoolLicenses
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.TenantId == tenantId, ct);
            schoolDetails = new
            {
                school_tenant_id = school.SchoolTenantId,
                curriculum_type = school.CurriculumType,
                grade_range_start = school.GradeRangeStart,
                grade_range_end = school.GradeRangeEnd,
                class_count = classCount,
                teacher_count = teacherCount,
                licence = licence is null ? null : (object)new
                {
                    plan_tier = licence.PlanTier,
                    seat_limit = licence.SeatLimit,
                    seats_used = licence.SeatsUsed,
                    subscription_end = licence.SubscriptionEnd,
                    is_trial = licence.IsTrial,
                },
            };
        }

        return Results.Ok(new
        {
            tenant_id = view.TenantId,
            tenant_name = school is null ? "Family Tenant" : school.SchoolNameEn,
            tenant_name_ar = school is null ? "حساب عائلة" : school.SchoolNameAr,
            tenant_type = view.TenantType,
            subscription = new
            {
                plan_name = plan?.PlanNameEn,
                plan_name_ar = plan?.PlanNameAr,
                tier = view.PlanTier,
                status = view.SubscriptionStatus,
                current_period_end = sub?.CurrentPeriodEnd,
                payment_history_summary = new
                {
                    total_paid_egp = Math.Round(paidTotal, 2),
                    last_payment_at = lastPayment,
                    failed_payments_count = failedPayments,
                },
            },
            usage = new
            {
                active_students = view.ActiveStudentCount,
                total_sessions_30d = view.MonthlySessionCount,
                total_ai_requests_30d = aiRequests30d,
                ai_cost_30d_egp = view.MonthlyAiCostEgp,
                storage_mb = view.StorageUsageMb,
            },
            engagement = new
            {
                engagement_score = view.EngagementScore,
                at_risk_students = view.AtRiskStudentCount,
                streak_distribution = Array.Empty<object>(),
            },
            school_details = schoolDetails,
            computed_at = view.ComputedAt,
        });
    }

    // ── T101 handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> ListFlagsAsync(
        HttpContext http,
        MuallimiDbContext db,
        Guid tenantId,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var flags = await db.FeatureFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId)
            .OrderBy(f => f.FlagName)
            .ToListAsync(ct);
        return Results.Ok(new
        {
            flags = flags.Select(f => new
            {
                flag_name = f.FlagName,
                is_enabled = f.IsEnabled,
                changed_by = f.ChangedByOperatorId.ToString(),
                changed_at = f.ChangedAt,
            }),
        });
    }

    private static async Task<IResult> SetFlagAsync(
        HttpContext http,
        MuallimiDbContext db,
        FeatureFlagService flags,
        AuditTrailWriter audit,
        Guid tenantId,
        string flagName,
        FeatureFlagToggleBody body,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        if (string.IsNullOrWhiteSpace(body.Reason))
        {
            return Results.BadRequest(new { error = "reason is required." });
        }

        var operatorId = ResolveOperatorId(http);
        var correlationId = ResolveCorrelation(http);

        var previous = await db.FeatureFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FlagName == flagName, ct);
        var previousValue = previous?.IsEnabled ?? false;

        await flags.SetAsync(tenantId, flagName, body.IsEnabled, operatorId, ct);

        var auditId = Guid.NewGuid();
        await audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = tenantId,
            ActorId = operatorId,
            ActorType = "operator",
            TargetType = "feature_flag",
            ActionType = "operator.feature_flag.toggled",
            Payload = new
            {
                flag_name = flagName,
                is_enabled = body.IsEnabled,
                previous_value = previousValue,
                reason = body.Reason,
            },
            CorrelationId = correlationId,
        }, ct);

        return Results.Ok(new
        {
            flag_name = flagName,
            is_enabled = body.IsEnabled,
            previous_value = previousValue,
            audit_entry_id = auditId,
        });
    }

    // ── T102 handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> StartImpersonationAsync(
        HttpContext http,
        ImpersonationService impersonation,
        ImpersonationStartBody body,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        if (string.IsNullOrWhiteSpace(body.Reason))
        {
            return Results.BadRequest(new { error = "reason is required." });
        }
        if (body.TargetRole is not ("parent" or "school_admin" or "teacher"))
        {
            return Results.BadRequest(new { error = "target_role must be parent, school_admin, or teacher." });
        }

        var operatorId = ResolveOperatorId(http);
        var correlationId = ResolveCorrelation(http);
        var result = await impersonation.StartAsync(
            operatorId,
            body.TargetTenantId,
            body.TargetRole,
            body.TargetUserId,
            body.Reason,
            correlationId,
            ct);

        return Results.Ok(new
        {
            impersonation_token = result.Token,
            expires_at = result.ExpiresAt,
            audit_entry_id = result.AuditEntryId,
        });
    }

    private static async Task<IResult> EndImpersonationAsync(
        HttpContext http,
        ImpersonationService impersonation,
        ImpersonationEndBody body,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var token = body.ImpersonationToken
            ?? http.Request.Headers["X-Impersonation-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.BadRequest(new { error = "impersonation_token is required." });
        }

        var correlationId = ResolveCorrelation(http);
        var result = await impersonation.EndAsync(token, correlationId, ct);
        if (result is null) return Results.NotFound(new { error = "No active impersonation session for token." });

        return Results.Ok(new
        {
            duration_seconds = result.DurationSeconds,
            actions_performed = result.ActionsPerformed,
            audit_entry_id = result.AuditEntryId,
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static Guid ResolveOperatorId(HttpContext http)
    {
        var raw = http.Request.Headers["X-Operator-Id"].FirstOrDefault();
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static string ResolveCorrelation(HttpContext http)
    {
        return http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    }
}

public sealed record FeatureFlagToggleBody(bool IsEnabled, string Reason);
public sealed record ImpersonationStartBody(Guid TargetTenantId, string TargetRole, Guid? TargetUserId, string Reason);
public sealed record ImpersonationEndBody(string? ImpersonationToken);
