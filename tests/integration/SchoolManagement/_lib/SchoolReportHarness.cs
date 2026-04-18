using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US9 school-report test harness.
///
/// Seeds a two-tenant topology (Alpha + Beta) with:
///   • One school per tenant, two Grade-7 classes in Alpha, one in Beta.
///   • 3 active students per class plus per-class SchoolAggregateView rows
///     with non-trivial AtRiskCount so the at_risk_distribution rollup has
///     signal to assert on.
///   • Per-(grade, subject) SchoolAggregateView rows so mastery_trends has
///     deterministic rows with an Arabic-formatted mastery score.
///   • One graded Exam + three scored submissions to populate
///     exam_performance.
/// </summary>
public sealed class SchoolReportHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0901");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0902");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0901");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0902");
    public static readonly Guid ClassAlpha7A = Guid.Parse("00000003-0000-0000-0000-00000000090A");
    public static readonly Guid ClassAlpha7B = Guid.Parse("00000003-0000-0000-0000-00000000090B");
    public static readonly Guid ClassBeta7A = Guid.Parse("00000003-0000-0000-0000-000000000902");
    public static readonly Guid SubjectMath = Guid.Parse("00000004-0000-0000-0000-000000000901");
    public static readonly Guid AdminAlpha = Guid.Parse("00000005-0000-0000-0000-000000000901");
    public static readonly Guid ExamAlpha = Guid.Parse("00000009-0000-0000-0000-000000000901");

    public readonly List<Guid> Alpha7AStudents = new();
    public readonly List<Guid> Alpha7BStudents = new();
    public readonly List<Guid> BetaStudents = new();

    private readonly MuallimiDbContext _db;

    public SchoolReportHarness(MuallimiDbContext db) => _db = db;

    public async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        SeedSchool(TenantAlpha, SchoolAlpha, "Alpha", now);

        SeedClass(TenantAlpha, SchoolAlpha, ClassAlpha7A, 7, "A", now);
        for (var i = 0; i < 3; i++)
        {
            var sid = Guid.Parse($"00000007-0000-0000-0000-09A000000{i:D3}");
            Alpha7AStudents.Add(sid);
            SeedStudent(TenantAlpha, sid, $"Alpha7A Student {i + 1}", now);
            SeedEnrolment(TenantAlpha, ClassAlpha7A, sid, now);
        }
        SeedAtRisk(TenantAlpha, Alpha7AStudents[0], now);

        SeedClass(TenantAlpha, SchoolAlpha, ClassAlpha7B, 7, "B", now);
        for (var i = 0; i < 3; i++)
        {
            var sid = Guid.Parse($"00000007-0000-0000-0000-09B000000{i:D3}");
            Alpha7BStudents.Add(sid);
            SeedStudent(TenantAlpha, sid, $"Alpha7B Student {i + 1}", now);
            SeedEnrolment(TenantAlpha, ClassAlpha7B, sid, now);
        }

        SeedAggregate(TenantAlpha, SchoolAlpha, "class", ClassAlpha7A, 7, null, 3, 0.72m, 1, now);
        SeedAggregate(TenantAlpha, SchoolAlpha, "class", ClassAlpha7B, 7, null, 3, 0.55m, 0, now);
        SeedAggregate(TenantAlpha, SchoolAlpha, "school", SchoolAlpha, 7, SubjectMath, 6, 0.65m, 0, now);

        SeedExam(TenantAlpha, SchoolAlpha, ExamAlpha, "امتحان الرياضيات", "Math Exam", SubjectMath, 7, now);
        SeedExamSubmission(TenantAlpha, ExamAlpha, Alpha7AStudents[0], 85m, now);
        SeedExamSubmission(TenantAlpha, ExamAlpha, Alpha7AStudents[1], 72m, now);
        SeedExamSubmission(TenantAlpha, ExamAlpha, Alpha7AStudents[2], 54m, now);

        // Beta tenant — cross-tenant isolation fixture.
        SeedSchool(TenantBeta, SchoolBeta, "Beta", now);
        SeedClass(TenantBeta, SchoolBeta, ClassBeta7A, 7, "A", now);
        for (var i = 0; i < 2; i++)
        {
            var sid = Guid.Parse($"00000008-0000-0000-0000-09A000000{i:D3}");
            BetaStudents.Add(sid);
            SeedStudent(TenantBeta, sid, $"Beta Student {i + 1}", now);
            SeedEnrolment(TenantBeta, ClassBeta7A, sid, now);
        }
        SeedAggregate(TenantBeta, SchoolBeta, "class", ClassBeta7A, 7, null, 2, 0.50m, 2, now);

        await _db.SaveChangesAsync();
    }

    public SchoolReport BuildReport(string reportType = "mastery_trends", string language = "ar", Guid? subjectFilter = null)
    {
        var now = DateTime.UtcNow;
        return new SchoolReport
        {
            SchoolReportId = Guid.NewGuid(),
            TenantId = TenantAlpha,
            SchoolTenantId = SchoolAlpha,
            GeneratedByAdminId = AdminAlpha,
            ReportType = reportType,
            GradeFilter = 7,
            SubjectFilter = subjectFilter,
            ClassFilter = null,
            WindowStart = now.AddDays(-30),
            WindowEnd = now.AddDays(1),
            Language = language,
            Status = "generating",
            CreatedAt = now,
        };
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
            SubscriptionStatus = "active",
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void SeedClass(Guid tenantId, Guid schoolTenantId, Guid classId, int grade, string section, DateTime now)
    {
        _db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = classId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = grade,
            SectionLabel = section,
            DisplayNameAr = $"صف {grade}{section}",
            DisplayNameEn = $"Grade {grade}{section}",
            SubjectBindings = "[]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void SeedStudent(Guid tenantId, Guid studentId, string displayName, DateTime now)
    {
        _db.StudentProfiles.Add(new StudentProfile
        {
            Id = studentId,
            TenantId = tenantId,
            DisplayName = displayName,
            CurriculumType = "moe",
            Grade = "7",
            PreferredLanguage = "ar",
            PlanTier = "school",
            ConsentState = "granted",
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void SeedEnrolment(Guid tenantId, Guid classId, Guid studentId, DateTime now)
    {
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

    private void SeedAggregate(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid scopeId,
        int? grade,
        Guid? subjectId,
        int activeStudents,
        decimal averageMastery,
        int atRiskCount,
        DateTime now)
    {
        _db.SchoolAggregateViews.Add(new SchoolAggregateView
        {
            AggregateViewId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            ScopeType = scopeType,
            ScopeId = scopeId,
            Grade = grade,
            SubjectId = subjectId,
            ActiveStudentCount = activeStudents,
            AverageMastery = averageMastery,
            AtRiskCount = atRiskCount,
            ActiveStreakCount = activeStudents / 2,
            BadgesAwardedCount = activeStudents,
            LastUpdatedAt = now,
            LastEventId = Guid.NewGuid(),
        });
    }

    private void SeedExam(Guid tenantId, Guid schoolTenantId, Guid examId, string titleAr, string titleEn, Guid subjectId, int grade, DateTime now)
    {
        _db.Exams.Add(new Exam
        {
            ExamId = examId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            CreatedByAdminId = AdminAlpha,
            TitleAr = titleAr,
            TitleEn = titleEn,
            SubjectId = subjectId,
            Grade = grade,
            Status = "graded",
            TotalPoints = 100m,
            CreatedAt = now.AddDays(-5),
            UpdatedAt = now,
        });
    }

    private void SeedExamSubmission(Guid tenantId, Guid examId, Guid studentId, decimal score, DateTime now)
    {
        _db.ExamSubmissions.Add(new ExamSubmission
        {
            ExamSubmissionId = Guid.NewGuid(),
            TenantId = tenantId,
            ExamId = examId,
            StudentId = studentId,
            Answers = "{}",
            Score = score,
            MaxScore = 100m,
            GradingStatus = "graded",
            StartedAt = now.AddHours(-2),
            SubmittedAt = now.AddHours(-1),
            GradedAt = now,
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
    }

    private void SeedAtRisk(Guid tenantId, Guid studentId, DateTime now)
    {
        _db.AtRiskFlags.Add(new AtRiskFlag
        {
            AtRiskFlagId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            ThresholdVersion = "v1",
            TriggeringEvidence = "{}",
            RaisedAt = now.AddDays(-2),
            ClearedAt = null,
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
    }
}
