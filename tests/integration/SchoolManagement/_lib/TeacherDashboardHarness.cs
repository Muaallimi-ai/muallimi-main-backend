using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US5 teacher-dashboard harness. Seeds:
///   • Two tenants (Alpha / Beta) — Beta exists so the tests prove tenant
///     isolation on the teacher surface.
///   • Per tenant: one school, two classes (A/B), two teachers.
///   • Teacher "Math" is assigned to Class A for Math only.
///   • Teacher "Unassigned" has no active assignments — used to prove the
///     empty-state path.
///   • Three students per class with mastery / focus-area / at-risk rows
///     so the service has something to roll up.
///   • One Phase 4 parent row per student with a plan_tier field — present
///     specifically so the privacy test can assert the teacher view does
///     NOT include it.
/// </summary>
public sealed class TeacherDashboardHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0511");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0512");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0511");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0512");
    public static readonly Guid ClassAlpha = Guid.Parse("00000003-0000-0000-0000-000000000511");
    public static readonly Guid ClassAlphaB = Guid.Parse("00000003-0000-0000-0000-000000000521");
    public static readonly Guid ClassBeta = Guid.Parse("00000003-0000-0000-0000-000000000512");
    public static readonly Guid TeacherMathAlpha = Guid.Parse("00000004-0000-0000-0000-000000000511");
    public static readonly Guid TeacherUnassignedAlpha = Guid.Parse("00000004-0000-0000-0000-000000000521");
    public static readonly Guid TeacherMathBeta = Guid.Parse("00000004-0000-0000-0000-000000000512");
    public static readonly Guid SubjectMath = Guid.Parse("00000030-0000-0000-0000-000000000001");
    public static readonly Guid SubjectArabic = Guid.Parse("00000030-0000-0000-0000-000000000002");

    private readonly MuallimiDbContext _db;

    public TeacherDashboardHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    public List<Guid> AlphaClassAStudents { get; } = new();
    public List<Guid> AlphaClassBStudents { get; } = new();
    public List<Guid> BetaClassStudents { get; } = new();

    public async Task SeedAsync(bool includeBeta = true)
    {
        SeedAlpha();
        if (includeBeta)
        {
            SeedBeta();
        }
        await _db.SaveChangesAsync();
    }

    private void SeedAlpha()
    {
        var now = DateTime.UtcNow;
        AddSchool(TenantAlpha, SchoolAlpha, "ألفا", "Alpha", now);
        AddClass(TenantAlpha, SchoolAlpha, ClassAlpha, "الصف السابع أ", "Grade 7A", now);
        AddClass(TenantAlpha, SchoolAlpha, ClassAlphaB, "الصف السابع ب", "Grade 7B", now);
        AddTeacher(TenantAlpha, SchoolAlpha, TeacherMathAlpha, "معلّم الرياضيات", "Math Teacher", now);
        AddTeacher(TenantAlpha, SchoolAlpha, TeacherUnassignedAlpha, "معلّم بلا إسناد", "Unassigned Teacher", now);
        AddAssignment(TenantAlpha, TeacherMathAlpha, ClassAlpha, SubjectMath, now);
        SeedStudents(TenantAlpha, ClassAlpha, AlphaClassAStudents, "A", now);
        SeedStudents(TenantAlpha, ClassAlphaB, AlphaClassBStudents, "B", now);
    }

    private void SeedBeta()
    {
        var now = DateTime.UtcNow;
        AddSchool(TenantBeta, SchoolBeta, "بيتا", "Beta", now);
        AddClass(TenantBeta, SchoolBeta, ClassBeta, "الصف السابع بيتا", "Grade 7 Beta", now);
        AddTeacher(TenantBeta, SchoolBeta, TeacherMathBeta, "معلّم بيتا", "Beta Teacher", now);
        AddAssignment(TenantBeta, TeacherMathBeta, ClassBeta, SubjectMath, now);
        SeedStudents(TenantBeta, ClassBeta, BetaClassStudents, "Beta", now);
    }

    private void AddSchool(Guid tenantId, Guid schoolTenantId, string ar, string en, DateTime now)
    {
        _db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = $"مدرسة {ar}",
            SchoolNameEn = $"{en} School",
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

    private void AddClass(Guid tenantId, Guid schoolTenantId, Guid classId, string nameAr, string nameEn, DateTime now)
    {
        _db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = classId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = 7,
            SectionLabel = nameEn.EndsWith("A") ? "A" : (nameEn.EndsWith("B") ? "B" : "X"),
            DisplayNameAr = nameAr,
            DisplayNameEn = nameEn,
            SubjectBindings = "[\"math\"]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private void AddTeacher(Guid tenantId, Guid schoolTenantId, Guid teacherId, string nameAr, string nameEn, DateTime now)
    {
        _db.Teachers.Add(new Teacher
        {
            TeacherId = teacherId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UserIdentityId = Guid.NewGuid(),
            DisplayNameAr = nameAr,
            DisplayNameEn = nameEn,
            CreatedAt = now,
            DeactivatedAt = null,
        });
    }

    private void AddAssignment(Guid tenantId, Guid teacherId, Guid classId, Guid subjectId, DateTime now)
    {
        _db.TeacherAssignments.Add(new Muallimi.Domain.SchoolManagement.TeacherAssignment
        {
            TeacherAssignmentId = Guid.NewGuid(),
            TenantId = tenantId,
            TeacherId = teacherId,
            ClassGroupId = classId,
            SubjectId = subjectId,
            AssignedAt = now,
            UnassignedAt = null,
        });
    }

    private void SeedStudents(Guid tenantId, Guid classId, List<Guid> studentIds, string prefix, DateTime now)
    {
        var scores = new[] { 0.20m, 0.55m, 0.85m };
        for (var i = 0; i < 3; i++)
        {
            var studentId = Guid.NewGuid();
            studentIds.Add(studentId);

            _db.StudentProfiles.Add(new StudentProfile
            {
                Id = studentId,
                TenantId = tenantId,
                DisplayName = $"{prefix}Student{i + 1}",
                CurriculumType = "moe",
                Grade = "7",
                PreferredLanguage = "ar",
                PlanTier = "premium",
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

            _db.MasteryStates.Add(new MasteryState
            {
                MasteryStateId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                CurriculumType = "moe",
                SubjectId = SubjectMath,
                TopicId = Guid.NewGuid(),
                MasteryScore = scores[i],
                MasteryBand = scores[i] switch
                {
                    < 0.25m => "introduced",
                    < 0.50m => "practicing",
                    < 0.75m => "on_track",
                    _ => "confident",
                },
                CalculationVersion = "v1",
                ContributingRecordCount = 10,
                LastUpdatedAt = now,
                LastCorrelationId = "corr-seed",
            });

            // Mastery row the teacher is NOT assigned to — proves scoping.
            _db.MasteryStates.Add(new MasteryState
            {
                MasteryStateId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                CurriculumType = "moe",
                SubjectId = SubjectArabic,
                TopicId = Guid.NewGuid(),
                MasteryScore = 0.90m,
                MasteryBand = "confident",
                CalculationVersion = "v1",
                ContributingRecordCount = 5,
                LastUpdatedAt = now,
                LastCorrelationId = "corr-seed",
            });

            _db.StreakStates.Add(new StreakState
            {
                StreakStateId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                CurrentLength = i == 0 ? 0 : 3,
                LongestLength = 5,
                LastQualifyingDay = now.Date,
                FamilyTimezone = "Asia/Dubai",
                ResetHistory = "[]",
                LastUpdatedAt = now,
            });
        }

        // Lowest scorer is at-risk.
        _db.AtRiskFlags.Add(new AtRiskFlag
        {
            AtRiskFlagId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentIds[0],
            ThresholdVersion = "v1",
            TriggeringEvidence = "{}",
            RaisedAt = now,
            ClearedAt = null,
            CorrelationId = "corr-seed",
        });

        // Focus area on the mid student, for the teacher's subject.
        _db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentIds[1],
            CurriculumType = "moe",
            SubjectId = SubjectMath,
            ChapterId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            SignalSummary = "{}",
            RationaleAr = "يحتاج إلى مراجعة قسمة الأعداد",
            RationaleEn = "Needs to revisit integer division",
            SuggestedNextStep = "{}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = now,
            ValidUntil = now.AddDays(7),
            CorrelationId = "corr-seed",
        });

        // Unrelated focus area in a subject the teacher isn't assigned to.
        _db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentIds[1],
            CurriculumType = "moe",
            SubjectId = SubjectArabic,
            ChapterId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            SignalSummary = "{}",
            RationaleAr = "عربي",
            RationaleEn = "Arabic",
            SuggestedNextStep = "{}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = now,
            ValidUntil = now.AddDays(7),
            CorrelationId = "corr-seed",
        });

        // One badge on the top student.
        _db.BadgeAwards.Add(new BadgeAward
        {
            BadgeAwardId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentIds[^1],
            BadgeCriterionId = Guid.NewGuid(),
            BadgeCriterionVersion = "v1",
            AwardedAt = now,
            OriginatingProgressRecordIds = "[]",
            CelebrationShown = true,
            CorrelationId = "corr-seed",
        });
    }

    public TeacherDashboardService BuildService()
    {
        var classes = new ClassGroupRepository(_db);
        var enrolments = new ClassEnrolmentRepository(_db);
        var assignments = new TeacherAssignmentRepository(_db);
        var teachers = new TeacherRepository(_db);
        return new TeacherDashboardService(_db, classes, enrolments, assignments, teachers);
    }

    public (TeacherAtRiskNotificationHook Hook, RecordingChannelAdapterRegistry Registry) BuildNotificationHook()
    {
        var registry = new RecordingChannelAdapterRegistry();
        return (new TeacherAtRiskNotificationHook(_db, registry), registry);
    }
}

/// <summary>
/// Test-only <see cref="INotificationChannelAdapterRegistry"/> that records
/// every dispatch request instead of calling the Phase 4 local HTTP stubs.
/// </summary>
public sealed class RecordingChannelAdapterRegistry : INotificationChannelAdapterRegistry
{
    private readonly RecordingChannelAdapter _adapter = new();
    public IReadOnlyList<NotificationDispatchRequest> Dispatches => _adapter.Dispatches;
    public INotificationChannelAdapter Get(string channel) => _adapter;

    private sealed class RecordingChannelAdapter : INotificationChannelAdapter
    {
        private readonly List<NotificationDispatchRequest> _dispatches = new();
        public IReadOnlyList<NotificationDispatchRequest> Dispatches => _dispatches;
        public string Channel => "in_app";

        public Task<NotificationDispatchReceipt> DispatchAsync(
            NotificationDispatchRequest request,
            CancellationToken ct = default)
        {
            _dispatches.Add(request);
            return Task.FromResult(new NotificationDispatchReceipt(
                ReceiptId: Guid.NewGuid().ToString("D"),
                Channel: Channel));
        }
    }
}
