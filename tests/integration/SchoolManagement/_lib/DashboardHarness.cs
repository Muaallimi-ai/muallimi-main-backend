using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.Phase4EventConsumer;
using Muallimi.Api.SchoolManagement.SchoolDashboard;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US4 dashboard harness — seeds one or more tenant/school pairs
/// with a single class, three students, Phase 4 mastery/streak/badge/focus
/// area rows, and one open at-risk flag. The second side ("Beta") exists
/// to prove tenant isolation at query time. Tests build the service stack
/// via <see cref="BuildService"/> or the updater via
/// <see cref="BuildUpdater"/> to keep the scenarios deterministic.
/// </summary>
public sealed class DashboardHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0411");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0412");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0411");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0412");
    public static readonly Guid AdminAlpha = Guid.Parse("00000001-0000-0000-0000-000000000411");
    public static readonly Guid AdminBeta = Guid.Parse("00000001-0000-0000-0000-000000000412");
    public static readonly Guid ClassAlpha = Guid.Parse("00000003-0000-0000-0000-000000000411");
    public static readonly Guid ClassBeta = Guid.Parse("00000003-0000-0000-0000-000000000412");
    public static readonly Guid SubjectMath = Guid.Parse("00000030-0000-0000-0000-000000000001");

    private readonly MuallimiDbContext _db;

    public DashboardHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    public List<Guid> AlphaStudentIds { get; } = new();
    public List<Guid> BetaStudentIds { get; } = new();

    public async Task SeedAsync(bool includeBeta = true)
    {
        await SeedSideAsync(TenantAlpha, SchoolAlpha, AdminAlpha, ClassAlpha, AlphaStudentIds, "ألفا", "Alpha");
        if (includeBeta)
        {
            await SeedSideAsync(TenantBeta, SchoolBeta, AdminBeta, ClassBeta, BetaStudentIds, "بيتا", "Beta");
        }
        await _db.SaveChangesAsync();
    }

    private Task SeedSideAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid adminId,
        Guid classGroupId,
        List<Guid> studentIds,
        string nameAr,
        string nameEn)
    {
        var now = DateTime.UtcNow;

        _db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = $"مدرسة {nameAr}",
            SchoolNameEn = $"{nameEn} School",
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

        _db.SchoolAdministrators.Add(new SchoolAdministrator
        {
            SchoolAdminId = adminId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UserIdentityId = Guid.NewGuid(),
            InvitationEmail = $"admin-{nameEn.ToLowerInvariant()}@example.test",
            OnboardingStatus = "onboarded",
            TermsAcceptedAt = now,
            CreatedAt = now,
        });

        _db.ClassGroups.Add(new ClassGroup
        {
            ClassGroupId = classGroupId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            Grade = 7,
            SectionLabel = "A",
            DisplayNameAr = $"الصف السابع {nameAr}",
            DisplayNameEn = $"Grade 7 {nameEn}",
            SubjectBindings = "[\"math\"]",
            AcademicYear = "2026-2027",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Three students with a spread of mastery scores so the band
        // classifier picks up different bands per run.
        var scores = new[] { 0.20m, 0.55m, 0.85m };
        for (var i = 0; i < 3; i++)
        {
            var studentId = Guid.NewGuid();
            studentIds.Add(studentId);

            _db.StudentProfiles.Add(new StudentProfile
            {
                Id = studentId,
                TenantId = tenantId,
                DisplayName = $"{nameEn}Student{i + 1}",
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
                ClassGroupId = classGroupId,
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
                TopicId = null,
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

        // One badge award for the top student.
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

        // One open at-risk flag on the lowest scorer.
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

        // One focus area for the mid student.
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
            RationaleAr = "rationale",
            RationaleEn = "rationale",
            SuggestedNextStep = "{}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = now,
            ValidUntil = now.AddDays(7),
            CorrelationId = "corr-seed",
        });

        return Task.CompletedTask;
    }

    public SchoolDashboardService BuildService()
    {
        var classes = new ClassGroupRepository(_db);
        var enrolments = new ClassEnrolmentRepository(_db);
        var aggregates = new SchoolAggregateViewRepository(_db);
        var cache = new SchoolDashboardQueryCache();
        return new SchoolDashboardService(_db, classes, enrolments, aggregates, cache);
    }

    public SchoolAggregateViewUpdater BuildUpdater()
        => new SchoolAggregateViewUpdater(_db, BuildService());

    public SchoolOperatorImpersonationAuditor BuildAuditor()
        => new SchoolOperatorImpersonationAuditor(_db);
}
