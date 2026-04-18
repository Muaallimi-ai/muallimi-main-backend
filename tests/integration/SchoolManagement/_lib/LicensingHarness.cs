using System;
using System.Threading.Tasks;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US10 licensing test harness.
///
/// Seeds two tenants with a single school each. Each school gets a baseline
/// license that tests can mutate. Alpha gets an active license with a
/// configurable seat limit + feature gates; Beta gets an expired license so
/// the expiry-gating tests can assert the two cases side-by-side without
/// cross-tenant contamination.
/// </summary>
public sealed class LicensingHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa1001");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb1002");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff1001");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff1002");
    public static readonly Guid OperatorActor = Guid.Parse("eeeeeeee-0000-0000-0000-000000001010");

    private readonly MuallimiDbContext _db;

    public LicensingHarness(MuallimiDbContext db) => _db = db;

    public async Task SeedAsync(
        int alphaSeatLimit = 50,
        int alphaSeatsUsed = 0,
        string alphaFeatureGates = "{\"exams\":true,\"announcements\":true,\"reports\":true,\"leaderboards\":true}",
        bool alphaIsTrial = true,
        DateTime? alphaSubscriptionEnd = null,
        DateTime? betaSubscriptionEnd = null)
    {
        var now = DateTime.UtcNow;

        SeedSchool(TenantAlpha, SchoolAlpha, "Alpha", now);
        SeedSchool(TenantBeta, SchoolBeta, "Beta", now);

        _db.SchoolLicenses.Add(new SchoolLicense
        {
            SchoolLicenseId = Guid.NewGuid(),
            TenantId = TenantAlpha,
            SchoolTenantId = SchoolAlpha,
            PlanTier = "starter",
            SeatLimit = alphaSeatLimit,
            SeatsUsed = alphaSeatsUsed,
            FeatureGates = alphaFeatureGates,
            SubscriptionStart = now.AddDays(-1),
            SubscriptionEnd = alphaSubscriptionEnd ?? now.AddDays(30),
            IsTrial = alphaIsTrial,
            SeatWarningThreshold = 90,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now,
        });

        _db.SchoolLicenses.Add(new SchoolLicense
        {
            SchoolLicenseId = Guid.NewGuid(),
            TenantId = TenantBeta,
            SchoolTenantId = SchoolBeta,
            PlanTier = "starter",
            SeatLimit = 20,
            SeatsUsed = 15,
            FeatureGates = "{\"exams\":true}",
            SubscriptionStart = now.AddDays(-60),
            SubscriptionEnd = betaSubscriptionEnd ?? now.AddDays(-1),
            IsTrial = false,
            SeatWarningThreshold = 90,
            CreatedAt = now.AddDays(-60),
            UpdatedAt = now,
        });

        await _db.SaveChangesAsync();
    }

    private void SeedSchool(Guid tenantId, Guid schoolTenantId, string prefix, DateTime now)
    {
        _db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = $"مدرسة {prefix}",
            SchoolNameEn = $"{prefix} School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            SubjectBindings = "[]",
            AcademicCalendar = "{}",
            PreferredLanguage = "ar",
            SubscriptionStatus = prefix == "Alpha" ? "trial" : "expired",
            CreatedByOperatorId = OperatorActor,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now,
        });
    }
}
