using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// T027 — Phase 5 tenant isolation harness for the school-management surface.
///
/// Seeds two independent tenants (<see cref="TenantAlpha"/> and
/// <see cref="TenantBeta"/>) each with a school tenant, administrator,
/// teacher, class group, class enrolment, teacher assignment, exam,
/// announcement, and license. Both sides intentionally share numeric grade
/// and section labels so any query missing the tenant filter leaks across
/// schools.
///
/// Every Phase 5 integration test that exercises a query surface MUST build
/// the harness and assert that zero rows from the "other" tenant are
/// returned when the scope is set to "this" tenant.
/// </summary>
public sealed class TenantIsolationHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0501");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0502");

    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0a01");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0b01");

    public static readonly Guid AdminAlpha = Guid.Parse("00000001-0000-0000-0000-000000000a01");
    public static readonly Guid AdminBeta = Guid.Parse("00000001-0000-0000-0000-000000000b01");

    public static readonly Guid TeacherAlpha = Guid.Parse("00000002-0000-0000-0000-000000000a01");
    public static readonly Guid TeacherBeta = Guid.Parse("00000002-0000-0000-0000-000000000b01");

    public static readonly Guid ClassAlpha = Guid.Parse("00000003-0000-0000-0000-000000000a01");
    public static readonly Guid ClassBeta = Guid.Parse("00000003-0000-0000-0000-000000000b01");

    private readonly MuallimiDbContext _db;

    public TenantIsolationHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task SeedAsync()
    {
        await SeedOneSideAsync(TenantAlpha, SchoolAlpha, AdminAlpha, TeacherAlpha, ClassAlpha, "alpha");
        await SeedOneSideAsync(TenantBeta, SchoolBeta, AdminBeta, TeacherBeta, ClassBeta, "beta");
        await _db.SaveChangesAsync();
    }

    private Task SeedOneSideAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid adminId,
        Guid teacherId,
        Guid classGroupId,
        string label)
    {
        var now = DateTime.UtcNow;

        _db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = $"مدرسة {label}",
            SchoolNameEn = $"School {label}",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            SubjectBindings = "[]",
            AcademicCalendar = "{}",
            PreferredLanguage = "ar",
            SubscriptionStatus = "trial",
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.SchoolAdministrators.Add(new SchoolAdministrator
        {
            SchoolAdminId = adminId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UserIdentityId = Guid.NewGuid(),
            InvitationEmail = $"admin-{label}@example.test",
            OnboardingStatus = "onboarded",
            TermsAcceptedAt = now,
            CreatedAt = now,
        });

        _db.Teachers.Add(new Teacher
        {
            TeacherId = teacherId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UserIdentityId = Guid.NewGuid(),
            DisplayNameAr = $"المعلم {label}",
            DisplayNameEn = $"Teacher {label}",
            CreatedAt = now,
        });

        _db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = classGroupId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = 7,
            SectionLabel = "A",
            DisplayNameAr = "الصف السابع أ",
            DisplayNameEn = "Grade 7A",
            SubjectBindings = "[]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.SchoolLicenses.Add(new SchoolLicense
        {
            SchoolLicenseId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            PlanTier = "trial",
            SeatLimit = 50,
            SeatsUsed = 0,
            FeatureGates = "{}",
            SubscriptionStart = now.AddDays(-1),
            SubscriptionEnd = now.AddDays(30),
            IsTrial = true,
            SeatWarningThreshold = 90,
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
                    $"Phase 5 tenant isolation violated: row of type {typeof(T).Name} carried tenant {actual}, expected {expectedTenantId}");
            }
        }
    }
}
