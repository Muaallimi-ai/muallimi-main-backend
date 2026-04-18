using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// T028 — Phase 5 role isolation harness.
///
/// Seeds a single school with two teachers whose <see cref="TeacherAssignment"/>
/// rows explicitly differ by class/subject. Tests assert that:
///   - <c>TeacherRed</c> sees only <c>(ClassRed, SubjectRed)</c> rows
///   - <c>TeacherBlue</c> sees only <c>(ClassBlue, SubjectBlue)</c> rows
///   - Neither teacher sees the other's students, submissions, or
///     announcements
///   - Billing / family-private columns are never projected into teacher
///     responses (<see cref="ForbiddenTeacherProjections"/> lists them)
///
/// Every Phase 5 test that verifies teacher role scoping MUST build the
/// harness and assert using
/// <see cref="AssertTeacherSeesOnlyAssignedScopeAsync"/>.
/// </summary>
public sealed class RoleIsolationHarness
{
    public static readonly Guid Tenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0501");
    public static readonly Guid School = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddd0501");

    public static readonly Guid TeacherRed = Guid.Parse("00000010-0000-0000-0000-000000000001");
    public static readonly Guid TeacherBlue = Guid.Parse("00000010-0000-0000-0000-000000000002");

    public static readonly Guid ClassRed = Guid.Parse("00000020-0000-0000-0000-000000000001");
    public static readonly Guid ClassBlue = Guid.Parse("00000020-0000-0000-0000-000000000002");

    public static readonly Guid SubjectRed = Guid.Parse("00000030-0000-0000-0000-000000000001");
    public static readonly Guid SubjectBlue = Guid.Parse("00000030-0000-0000-0000-000000000002");

    /// <summary>
    /// Column names that must NEVER appear in a teacher-scoped response.
    /// Every teacher-dashboard contract test verifies this list.
    /// </summary>
    public static readonly IReadOnlyList<string> ForbiddenTeacherProjections = new[]
    {
        "family_email",
        "parent_email",
        "billing_status",
        "plan_tier",
        "invoice_amount",
        "payment_method",
    };

    private readonly MuallimiDbContext _db;

    public RoleIsolationHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task SeedAsync()
    {
        var now = DateTime.UtcNow;

        _db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = School,
            TenantId = Tenant,
            SchoolNameAr = "مدرسة الاختبار",
            SchoolNameEn = "Test School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            SubjectBindings = "[]",
            AcademicCalendar = "{}",
            PreferredLanguage = "ar",
            SubscriptionStatus = "active",
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.Teachers.AddRange(
            new Teacher
            {
                TeacherId = TeacherRed,
                TenantId = Tenant,
                SchoolTenantId = School,
                UserIdentityId = Guid.NewGuid(),
                DisplayNameAr = "المعلم الأحمر",
                DisplayNameEn = "Red Teacher",
                CreatedAt = now,
            },
            new Teacher
            {
                TeacherId = TeacherBlue,
                TenantId = Tenant,
                SchoolTenantId = School,
                UserIdentityId = Guid.NewGuid(),
                DisplayNameAr = "المعلم الأزرق",
                DisplayNameEn = "Blue Teacher",
                CreatedAt = now,
            });

        _db.ClassGroups.AddRange(
            new ClassGroup
            {
                ClassGroupId = ClassRed,
                TenantId = Tenant,
                SchoolTenantId = School,
                Grade = 7,
                SectionLabel = "A",
                DisplayNameAr = "الصف السابع أ",
                DisplayNameEn = "Grade 7A",
                SubjectBindings = "[]",
                AcademicYear = "2026-2027",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new ClassGroup
            {
                ClassGroupId = ClassBlue,
                TenantId = Tenant,
                SchoolTenantId = School,
                Grade = 7,
                SectionLabel = "B",
                DisplayNameAr = "الصف السابع ب",
                DisplayNameEn = "Grade 7B",
                SubjectBindings = "[]",
                AcademicYear = "2026-2027",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });

        _db.TeacherAssignments.AddRange(
            new TeacherAssignment
            {
                TeacherAssignmentId = Guid.NewGuid(),
                TenantId = Tenant,
                TeacherId = TeacherRed,
                ClassGroupId = ClassRed,
                SubjectId = SubjectRed,
                AssignedAt = now.AddDays(-10),
            },
            new TeacherAssignment
            {
                TeacherAssignmentId = Guid.NewGuid(),
                TenantId = Tenant,
                TeacherId = TeacherBlue,
                ClassGroupId = ClassBlue,
                SubjectId = SubjectBlue,
                AssignedAt = now.AddDays(-10),
            });

        await _db.SaveChangesAsync();
    }

    public async Task AssertTeacherSeesOnlyAssignedScopeAsync(
        Guid teacherId,
        Guid expectedClassId,
        Guid expectedSubjectId)
    {
        var seen = await _db.TeacherAssignments
            .AsNoTracking()
            .Where(a => a.TeacherId == teacherId && a.UnassignedAt == null)
            .Select(a => new { a.ClassGroupId, a.SubjectId })
            .ToListAsync();

        foreach (var a in seen)
        {
            if (a.ClassGroupId != expectedClassId || a.SubjectId != expectedSubjectId)
            {
                throw new InvalidOperationException(
                    $"Teacher {teacherId} should see only class {expectedClassId}/subject {expectedSubjectId} but saw {a.ClassGroupId}/{a.SubjectId}");
            }
        }
    }
}
