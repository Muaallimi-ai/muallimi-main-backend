using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US7 leaderboard harness. Seeds:
///   • Two tenants (Alpha / Beta) for tenant-isolation assertions.
///   • Per tenant: one school, one class, one teacher assigned to math,
///     four students enrolled in the class.
///   • One parent in tenant Alpha linked to the first Alpha student.
///   • Mastery states per student in math with deterministic scores that
///     exercise ranking + ties (student 2 and student 3 have equal
///     mastery in the core fixture).
/// </summary>
public sealed class LeaderboardHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0701");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0702");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0701");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0702");
    public static readonly Guid ClassAlpha = Guid.Parse("00000003-0000-0000-0000-000000000701");
    public static readonly Guid ClassBeta = Guid.Parse("00000003-0000-0000-0000-000000000702");
    public static readonly Guid TeacherAlpha = Guid.Parse("00000004-0000-0000-0000-000000000701");
    public static readonly Guid AdminAlpha = Guid.Parse("00000005-0000-0000-0000-000000000701");
    public static readonly Guid SubjectMath = Guid.Parse("00000030-0000-0000-0000-000000000701");
    public static readonly Guid ParentAlpha = Guid.Parse("00000006-0000-0000-0000-000000000701");

    public Guid Student1 { get; } = Guid.Parse("00000007-0000-0000-0000-000000000701");
    public Guid Student2 { get; } = Guid.Parse("00000007-0000-0000-0000-000000000702");
    public Guid Student3 { get; } = Guid.Parse("00000007-0000-0000-0000-000000000703");
    public Guid Student4 { get; } = Guid.Parse("00000007-0000-0000-0000-000000000704");

    private readonly MuallimiDbContext _db;

    public LeaderboardHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Seed the primary tenant + optionally a sibling tenant. <paramref name="masteryScores"/>
    /// are keyed by the four Alpha students in order — pass <c>null</c> to skip mastery rows.
    /// </summary>
    public async Task SeedAsync(decimal[]? masteryScores = null, bool includeBeta = false)
    {
        var now = DateTime.UtcNow;
        SeedTenant(TenantAlpha, SchoolAlpha, ClassAlpha, TeacherAlpha, "Alpha", now,
            new[] { Student1, Student2, Student3, Student4 },
            new[] { "Layla Ahmed", "Noor Ibrahim", "Salma Khaled", "Yusuf Omar" });
        if (masteryScores is not null)
        {
            var ids = new[] { Student1, Student2, Student3, Student4 };
            for (var i = 0; i < ids.Length && i < masteryScores.Length; i++)
            {
                _db.MasteryStates.Add(new MasteryState
                {
                    MasteryStateId = Guid.NewGuid(),
                    TenantId = TenantAlpha,
                    StudentId = ids[i],
                    CurriculumType = "moe",
                    SubjectId = SubjectMath,
                    TopicId = null,
                    MasteryScore = masteryScores[i],
                    MasteryBand = "developing",
                    CalculationVersion = "v1",
                    ContributingRecordCount = 5,
                    LastUpdatedAt = now,
                    LastCorrelationId = Guid.NewGuid().ToString("D"),
                });
            }
        }

        _db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = ParentAlpha,
            TenantId = TenantAlpha,
            IdentityId = Guid.NewGuid(),
            PreferredLanguage = "ar",
            Locale = "ar-SA",
            Timezone = "Asia/Dubai",
            NotificationChannels = "{\"in_app\":true}",
            QuietHours = "{}",
            PerChildOverrides = "{}",
            ConsentState = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = TenantAlpha,
            ParentProfileId = ParentAlpha,
            StudentId = Student1,
            Role = "guardian",
            EffectiveStart = now.AddDays(-30),
            EffectiveEnd = null,
            CreatedAt = now,
            UpdatedAt = now,
        });

        if (includeBeta)
        {
            var betaStudents = new[]
            {
                Guid.Parse("00000008-0000-0000-0000-000000000702"),
                Guid.Parse("00000009-0000-0000-0000-000000000702"),
            };
            SeedTenant(TenantBeta, SchoolBeta, ClassBeta, Guid.NewGuid(), "Beta", now,
                betaStudents,
                new[] { "BetaStudent1", "BetaStudent2" });
        }

        await _db.SaveChangesAsync();
    }

    private void SeedTenant(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classId,
        Guid teacherId,
        string prefix,
        DateTime now,
        Guid[] studentIds,
        string[] displayNames)
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
            SubscriptionStatus = "active",
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = classId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = 7,
            SectionLabel = "A",
            DisplayNameAr = $"صف {prefix}",
            DisplayNameEn = $"{prefix} Class",
            SubjectBindings = "[\"math\"]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Teachers.Add(new Teacher
        {
            TeacherId = teacherId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UserIdentityId = Guid.NewGuid(),
            DisplayNameAr = $"معلم {prefix}",
            DisplayNameEn = $"{prefix} Teacher",
            CreatedAt = now,
            DeactivatedAt = null,
        });
        _db.TeacherAssignments.Add(new Muallimi.Domain.SchoolManagement.TeacherAssignment
        {
            TeacherAssignmentId = Guid.NewGuid(),
            TenantId = tenantId,
            TeacherId = teacherId,
            ClassGroupId = classId,
            SubjectId = SubjectMath,
            AssignedAt = now,
        });

        for (var i = 0; i < studentIds.Length; i++)
        {
            var studentId = studentIds[i];
            var name = i < displayNames.Length ? displayNames[i] : $"{prefix}Student{i + 1}";
            _db.StudentProfiles.Add(new StudentProfile
            {
                Id = studentId,
                TenantId = tenantId,
                DisplayName = name,
                CurriculumType = "moe",
                Grade = "7",
                PreferredLanguage = "ar",
                PlanTier = "school",
                ConsentState = "granted",
                CreatedAt = now,
                UpdatedAt = now,
            });
            _db.ClassEnrolments.Add(new ClassEnrolment
            {
                ClassEnrolmentId = Guid.NewGuid(),
                TenantId = tenantId,
                ClassGroupId = classId,
                StudentId = studentId,
                EnrolledAt = now,
                Status = "active",
            });
        }
    }

    public async Task UpsertConfigAsync(string privacyMode, bool enabled = true, string perClassOverridesJson = "[]")
    {
        _db.LeaderboardConfigs.Add(new LeaderboardConfig
        {
            LeaderboardConfigId = Guid.NewGuid(),
            TenantId = TenantAlpha,
            SchoolTenantId = SchoolAlpha,
            PrivacyMode = privacyMode,
            LeaderboardEnabled = enabled,
            PerClassOverridesJson = perClassOverridesJson,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }
}
