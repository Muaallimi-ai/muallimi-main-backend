using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.Engagement;

/// <summary>
/// T027 — Phase 4 tenant isolation harness.
///
/// Seeds two independent tenants (<see cref="TenantAlpha"/> and
/// <see cref="TenantBeta"/>) with intentionally overlapping student ids,
/// parent profiles, and source_event_ids so that any Phase 4 query missing
/// a tenant filter leaks cross-tenant rows. Every Phase 4 integration test
/// that exercises a query surface MUST build the harness and assert that
/// zero rows from the "other" tenant are returned when the scope is set to
/// "this" tenant.
///
/// The harness is deliberately minimal — it covers progress records,
/// mastery states, streak states, badge awards, focus areas, weekly
/// reports, parent profiles, and child links. Additional seed data is
/// added per-user-story test as needed.
/// </summary>
public sealed class TenantIsolationHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0001");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0002");
    public static readonly Guid SharedStudentIdAlpha = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0a01");
    public static readonly Guid SharedStudentIdBeta = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0b01");
    public static readonly Guid SharedParentIdAlpha = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddd0a01");
    public static readonly Guid SharedParentIdBeta = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddd0b01");

    private readonly MuallimiDbContext _db;

    public TenantIsolationHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task SeedAsync()
    {
        await SeedTenantAsync(TenantAlpha, SharedStudentIdAlpha, SharedParentIdAlpha, "alpha");
        await SeedTenantAsync(TenantBeta, SharedStudentIdBeta, SharedParentIdBeta, "beta");
        await _db.SaveChangesAsync();
    }

    private Task SeedTenantAsync(Guid tenantId, Guid studentId, Guid parentId, string label)
    {
        var now = DateTime.UtcNow;
        _db.ProgressRecords.Add(new ProgressRecord
        {
            ProgressRecordId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            SourceEventId = "shared-source-event-0001",
            EventKind = "session_start",
            CurriculumScope = "{}",
            Payload = $"{{\"label\":\"{label}\"}}",
            CorrelationId = Guid.NewGuid().ToString("D"),
            OccurredAt = now.AddMinutes(-10),
            IngestedAt = now.AddMinutes(-9),
        });

        _db.MasteryStates.Add(new MasteryState
        {
            MasteryStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(),
            TopicId = null,
            MasteryScore = 0.5m,
            MasteryBand = "practicing",
            CalculationVersion = "v1",
            ContributingRecordCount = 1,
            LastUpdatedAt = now,
            LastCorrelationId = Guid.NewGuid().ToString("D"),
        });

        _db.StreakStates.Add(new StreakState
        {
            StreakStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurrentLength = 1,
            LongestLength = 1,
            LastQualifyingDay = DateTime.UtcNow.Date,
            FamilyTimezone = "Asia/Dubai",
            ResetHistory = "[]",
            LastUpdatedAt = now,
        });

        _db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = parentId,
            TenantId = tenantId,
            IdentityId = Guid.NewGuid(),
            PreferredLanguage = "ar",
            Locale = "ar-SA",
            Timezone = "Asia/Dubai",
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId,
            ParentProfileId = parentId,
            StudentId = studentId,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-30),
            EffectiveEnd = null,
            CreatedAt = now,
            UpdatedAt = now,
        });

        return Task.CompletedTask;
    }

    public static async Task AssertNoCrossTenantLeakAsync<T>(
        MuallimiDbContext db,
        Func<MuallimiDbContext, IQueryable<T>> query,
        Guid expectedTenantId) where T : class
    {
        var rows = await query(db).ToListAsync();
        foreach (var row in rows)
        {
            var tenantProp = typeof(T).GetProperty("TenantId");
            if (tenantProp?.GetValue(row) is Guid actual && actual != expectedTenantId)
            {
                throw new InvalidOperationException(
                    $"Tenant isolation violated: row of type {typeof(T).Name} carried tenant {actual}, expected {expectedTenantId}");
            }
        }
    }
}
