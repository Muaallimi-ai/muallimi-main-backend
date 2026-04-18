using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

/// <summary>
/// T036 + T041 (US1) — <c>SchoolTenantProvisioningService</c>.
///
/// Orchestrates three operations:
///   1. <c>CreateAsync</c> — operator creates a new school tenant. Writes
///      the aggregate and enqueues a <c>school_created</c> downstream
///      event inside the same unit of work so downstream consumers see the
///      event iff the tenant row actually persists.
///   2. <c>UpdateConfigurationAsync</c> — school admin updates calendar,
///      language, or subject bindings on an existing tenant.
///   3. <c>GetConfigurationAsync</c> — read path used by the school admin
///      portal.
/// </summary>
public sealed record SchoolTenantCreateInput(
    string SchoolNameAr,
    string SchoolNameEn,
    string CurriculumType,
    int GradeRangeStart,
    int GradeRangeEnd,
    IReadOnlyList<string> SubjectBindings,
    object AcademicCalendar,
    string PreferredLanguage,
    Guid CreatedByOperatorId,
    Guid TenantId);

public sealed record SchoolTenantUpdateInput(
    IReadOnlyList<string>? SubjectBindings,
    object? AcademicCalendar,
    string? PreferredLanguage);

public interface ISchoolTenantProvisioningService
{
    Task<SchoolTenant> CreateAsync(
        SchoolTenantCreateInput input,
        string correlationId,
        CancellationToken ct = default);

    Task<SchoolTenant?> GetConfigurationAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<SchoolTenant?> UpdateConfigurationAsync(
        Guid tenantId,
        Guid schoolTenantId,
        SchoolTenantUpdateInput input,
        CancellationToken ct = default);
}

public sealed class SchoolTenantProvisioningService : ISchoolTenantProvisioningService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ISchoolTenantRepository _repo;
    private readonly IPhase5DownstreamEventOutbox _outbox;

    public SchoolTenantProvisioningService(
        ISchoolTenantRepository repo,
        IPhase5DownstreamEventOutbox outbox)
    {
        _repo = repo;
        _outbox = outbox;
    }

    public async Task<SchoolTenant> CreateAsync(
        SchoolTenantCreateInput input,
        string correlationId,
        CancellationToken ct = default)
    {
        if (input.GradeRangeEnd < input.GradeRangeStart)
            throw new ArgumentException("grade_range_end must be >= grade_range_start", nameof(input));

        var now = DateTime.UtcNow;
        var tenant = new SchoolTenant
        {
            SchoolTenantId = Guid.NewGuid(),
            TenantId = input.TenantId,
            SchoolNameAr = input.SchoolNameAr,
            SchoolNameEn = input.SchoolNameEn,
            CurriculumType = input.CurriculumType,
            GradeRangeStart = input.GradeRangeStart,
            GradeRangeEnd = input.GradeRangeEnd,
            SubjectBindings = JsonSerializer.Serialize(input.SubjectBindings, JsonOptions),
            AcademicCalendar = JsonSerializer.Serialize(input.AcademicCalendar, JsonOptions),
            PreferredLanguage = input.PreferredLanguage,
            SubscriptionStatus = "trial",
            CreatedByOperatorId = input.CreatedByOperatorId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repo.AddAsync(tenant, ct);

        await _outbox.EnqueueAsync(
            Phase5DownstreamEventKind.school_created,
            tenantId: tenant.TenantId,
            schoolTenantId: tenant.SchoolTenantId,
            payload: new
            {
                school_tenant_id = tenant.SchoolTenantId,
                tenant_id = tenant.TenantId,
                school_name_ar = tenant.SchoolNameAr,
                school_name_en = tenant.SchoolNameEn,
                curriculum_type = tenant.CurriculumType,
                grade_range_start = tenant.GradeRangeStart,
                grade_range_end = tenant.GradeRangeEnd,
                preferred_language = tenant.PreferredLanguage,
                subscription_status = tenant.SubscriptionStatus,
                created_by_operator_id = tenant.CreatedByOperatorId,
                created_at = tenant.CreatedAt,
            },
            correlationId: correlationId,
            occurredAt: now,
            ct: ct);

        await _repo.SaveChangesAsync(ct);
        return tenant;
    }

    public Task<SchoolTenant?> GetConfigurationAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => _repo.GetByIdAsync(tenantId, schoolTenantId, ct);

    public async Task<SchoolTenant?> UpdateConfigurationAsync(
        Guid tenantId,
        Guid schoolTenantId,
        SchoolTenantUpdateInput input,
        CancellationToken ct = default)
    {
        var tenant = await _repo.GetByIdForOperatorAsync(schoolTenantId, ct);
        if (tenant is null || tenant.TenantId != tenantId) return null;

        if (input.SubjectBindings is not null)
            tenant.SubjectBindings = JsonSerializer.Serialize(input.SubjectBindings, JsonOptions);
        if (input.AcademicCalendar is not null)
            tenant.AcademicCalendar = JsonSerializer.Serialize(input.AcademicCalendar, JsonOptions);
        if (!string.IsNullOrWhiteSpace(input.PreferredLanguage))
            tenant.PreferredLanguage = input.PreferredLanguage;
        tenant.UpdatedAt = DateTime.UtcNow;

        await _repo.SaveChangesAsync(ct);
        return tenant;
    }
}

public static class SchoolTenantProvisioningServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolTenantProvisioningService(this IServiceCollection services)
    {
        services.AddScoped<ISchoolTenantProvisioningService, SchoolTenantProvisioningService>();
        return services;
    }
}
