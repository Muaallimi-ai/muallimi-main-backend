using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T188 + T192 (US10) — <c>LicenseManagementService</c>.
///
/// Orchestrates license lifecycle: create, update, extend trial, and seat
/// count operations. Every mutation emits a <c>license_updated</c>
/// downstream event in the same unit of work.
///
/// Seat count is maintained atomically — callers MUST use
/// <see cref="IncrementSeatsUsedAsync"/> during roster import to prevent
/// concurrent over-provisioning.
/// </summary>
public sealed record LicenseCreateInput(
    Guid SchoolTenantId,
    Guid TenantId,
    string PlanTier,
    int SeatLimit,
    string FeatureGates,
    DateTime SubscriptionStart,
    DateTime SubscriptionEnd,
    bool IsTrial);

public sealed record LicenseUpdateInput(
    string? PlanTier,
    int? SeatLimit,
    string? FeatureGates,
    DateTime? SubscriptionEnd,
    bool? IsTrial);

public sealed record SeatIncrementResult(bool Allowed, int SeatsUsed, int SeatLimit, bool WarningTripped, bool LimitReached);

public interface ILicenseManagementService
{
    Task<SchoolLicense> CreateAsync(LicenseCreateInput input, string correlationId, CancellationToken ct = default);

    Task<SchoolLicense?> GetForSchoolAdminAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<SchoolLicense?> GetForOperatorAsync(Guid schoolTenantId, CancellationToken ct = default);

    Task<IReadOnlyList<SchoolLicense>> ListForOperatorAsync(CancellationToken ct = default);

    Task<SchoolLicense?> UpdateAsync(Guid schoolTenantId, LicenseUpdateInput input, string correlationId, CancellationToken ct = default);

    Task<SchoolLicense?> ExtendTrialAsync(Guid schoolTenantId, DateTime newEnd, string correlationId, CancellationToken ct = default);

    Task<int> GetSeatsUsedAsync(Guid schoolTenantId, CancellationToken ct = default);

    Task<SeatIncrementResult> IncrementSeatsUsedAsync(Guid schoolTenantId, int delta, string correlationId, CancellationToken ct = default);
}

public sealed class LicenseManagementService : ILicenseManagementService
{
    private readonly ISchoolLicenseRepository _repo;
    private readonly IPhase5DownstreamEventOutbox _outbox;
    private readonly ISchoolTenantRepository _schoolRepo;
    private readonly ISeatWarningNotifier _seatNotifier;

    public LicenseManagementService(
        ISchoolLicenseRepository repo,
        IPhase5DownstreamEventOutbox outbox,
        ISchoolTenantRepository schoolRepo,
        ISeatWarningNotifier seatNotifier)
    {
        _repo = repo;
        _outbox = outbox;
        _schoolRepo = schoolRepo;
        _seatNotifier = seatNotifier;
    }

    public async Task<SchoolLicense> CreateAsync(
        LicenseCreateInput input,
        string correlationId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var license = new SchoolLicense
        {
            SchoolLicenseId = Guid.NewGuid(),
            TenantId = input.TenantId,
            SchoolTenantId = input.SchoolTenantId,
            PlanTier = input.PlanTier,
            SeatLimit = input.SeatLimit,
            SeatsUsed = 0,
            FeatureGates = input.FeatureGates,
            SubscriptionStart = input.SubscriptionStart.ToUniversalTime(),
            SubscriptionEnd = input.SubscriptionEnd.ToUniversalTime(),
            IsTrial = input.IsTrial,
            SeatWarningThreshold = 90,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repo.AddAsync(license, ct);

        await EmitLicenseUpdatedAsync(license, "created", correlationId, now, ct);

        // Update school tenant subscription status
        var school = await _schoolRepo.GetByIdForOperatorAsync(input.SchoolTenantId, ct);
        if (school is not null)
        {
            school.SubscriptionStatus = input.IsTrial ? "trial" : "active";
            school.UpdatedAt = now;
        }

        await _repo.SaveChangesAsync(ct);
        return license;
    }

    public Task<SchoolLicense?> GetForSchoolAdminAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => _repo.GetBySchoolTenantIdAsync(tenantId, schoolTenantId, ct);

    public Task<SchoolLicense?> GetForOperatorAsync(Guid schoolTenantId, CancellationToken ct = default)
        => _repo.GetBySchoolTenantIdForOperatorAsync(schoolTenantId, ct);

    public Task<IReadOnlyList<SchoolLicense>> ListForOperatorAsync(CancellationToken ct = default)
        => _repo.ListForOperatorAsync(ct);

    public async Task<SchoolLicense?> UpdateAsync(
        Guid schoolTenantId,
        LicenseUpdateInput input,
        string correlationId,
        CancellationToken ct = default)
    {
        var license = await _repo.GetBySchoolTenantIdForOperatorAsync(schoolTenantId, ct);
        if (license is null) return null;

        var now = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(input.PlanTier))
            license.PlanTier = input.PlanTier;
        if (input.SeatLimit.HasValue)
            license.SeatLimit = input.SeatLimit.Value;
        if (!string.IsNullOrWhiteSpace(input.FeatureGates))
            license.FeatureGates = input.FeatureGates;
        if (input.SubscriptionEnd.HasValue)
            license.SubscriptionEnd = input.SubscriptionEnd.Value.ToUniversalTime();
        if (input.IsTrial.HasValue)
            license.IsTrial = input.IsTrial.Value;

        license.UpdatedAt = now;

        // Sync school tenant subscription status
        var school = await _schoolRepo.GetByIdForOperatorAsync(schoolTenantId, ct);
        if (school is not null)
        {
            if (license.SubscriptionEnd <= now)
                school.SubscriptionStatus = "expired";
            else if (license.IsTrial)
                school.SubscriptionStatus = "trial";
            else
                school.SubscriptionStatus = "active";
            school.UpdatedAt = now;
        }

        await EmitLicenseUpdatedAsync(license, "updated", correlationId, now, ct);
        await _repo.SaveChangesAsync(ct);
        return license;
    }

    public async Task<int> GetSeatsUsedAsync(Guid schoolTenantId, CancellationToken ct = default)
    {
        var license = await _repo.GetBySchoolTenantIdForOperatorAsync(schoolTenantId, ct);
        return license?.SeatsUsed ?? 0;
    }

    public async Task<SchoolLicense?> ExtendTrialAsync(
        Guid schoolTenantId,
        DateTime newEnd,
        string correlationId,
        CancellationToken ct = default)
    {
        var license = await _repo.GetBySchoolTenantIdForOperatorAsync(schoolTenantId, ct);
        if (license is null) return null;

        var now = DateTime.UtcNow;
        license.SubscriptionEnd = newEnd.ToUniversalTime();
        license.UpdatedAt = now;

        var school = await _schoolRepo.GetByIdForOperatorAsync(schoolTenantId, ct);
        if (school is not null)
        {
            school.SubscriptionStatus = license.IsTrial ? "trial" : "active";
            school.UpdatedAt = now;
        }

        await EmitLicenseUpdatedAsync(license, "trial_extended", correlationId, now, ct);
        await _repo.SaveChangesAsync(ct);
        return license;
    }

    public async Task<SeatIncrementResult> IncrementSeatsUsedAsync(
        Guid schoolTenantId,
        int delta,
        string correlationId,
        CancellationToken ct = default)
    {
        if (delta <= 0)
            throw new ArgumentOutOfRangeException(nameof(delta), "Seat delta must be positive.");

        var license = await _repo.GetBySchoolTenantIdForOperatorAsync(schoolTenantId, ct);
        if (license is null)
            return new SeatIncrementResult(false, 0, 0, false, true);

        if (license.SeatLimit > 0 && license.SeatsUsed + delta > license.SeatLimit)
        {
            return new SeatIncrementResult(
                Allowed: false,
                SeatsUsed: license.SeatsUsed,
                SeatLimit: license.SeatLimit,
                WarningTripped: false,
                LimitReached: true);
        }

        var now = DateTime.UtcNow;
        license.SeatsUsed += delta;
        license.UpdatedAt = now;

        var usagePct = license.SeatLimit > 0
            ? (int)((double)license.SeatsUsed / license.SeatLimit * 100)
            : 0;
        var warning = license.SeatLimit > 0 && usagePct >= license.SeatWarningThreshold;
        var limitReached = license.SeatLimit > 0 && license.SeatsUsed >= license.SeatLimit;

        await _seatNotifier.CheckAndNotifyAsync(license, ct);
        await EmitLicenseUpdatedAsync(license, "seats_incremented", correlationId, now, ct);
        await _repo.SaveChangesAsync(ct);

        return new SeatIncrementResult(
            Allowed: true,
            SeatsUsed: license.SeatsUsed,
            SeatLimit: license.SeatLimit,
            WarningTripped: warning,
            LimitReached: limitReached);
    }

    private Task EmitLicenseUpdatedAsync(
        SchoolLicense license, string action, string correlationId, DateTime now, CancellationToken ct)
    {
        return _outbox.EnqueueAsync(
            Phase5DownstreamEventKind.license_updated,
            tenantId: license.TenantId,
            schoolTenantId: license.SchoolTenantId,
            payload: new
            {
                school_license_id = license.SchoolLicenseId,
                school_tenant_id = license.SchoolTenantId,
                action,
                plan_tier = license.PlanTier,
                seat_limit = license.SeatLimit,
                seats_used = license.SeatsUsed,
                subscription_start = license.SubscriptionStart,
                subscription_end = license.SubscriptionEnd,
                is_trial = license.IsTrial,
                feature_gates = license.FeatureGates,
            },
            correlationId: correlationId,
            occurredAt: now,
            ct: ct);
    }
}

public static class LicenseManagementServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5LicenseManagementService(this IServiceCollection services)
    {
        services.AddScoped<ILicenseManagementService, LicenseManagementService>();
        return services;
    }
}
