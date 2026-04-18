using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Api.Exams.ExamAdministration;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Exams.ExamResults;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// Phase 5 US6 exam harness. Seeds:
///   • Two tenants (Alpha / Beta) — Beta exists so the tenant-isolation
///     contract tests can assert isolation on the exam surface.
///   • Per tenant: one school, one class, one teacher assigned to
///     mathematics, three students enrolled in that class.
///   • Students start with no mastery rows; exam grading drives the
///     Phase 4 session-event outbox writes.
/// </summary>
public sealed class ExamHarness
{
    public static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0611");
    public static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0612");
    public static readonly Guid SchoolAlpha = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0611");
    public static readonly Guid SchoolBeta = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffff0612");
    public static readonly Guid ClassAlpha = Guid.Parse("00000003-0000-0000-0000-000000000611");
    public static readonly Guid ClassBeta = Guid.Parse("00000003-0000-0000-0000-000000000612");
    public static readonly Guid TeacherAlpha = Guid.Parse("00000004-0000-0000-0000-000000000611");
    public static readonly Guid TeacherBeta = Guid.Parse("00000004-0000-0000-0000-000000000612");
    public static readonly Guid AdminAlpha = Guid.Parse("00000005-0000-0000-0000-000000000611");
    public static readonly Guid SubjectMath = Guid.Parse("00000030-0000-0000-0000-000000000001");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public ExamHarness(MuallimiDbContext db)
    {
        _db = db;
    }

    public List<Guid> AlphaStudents { get; } = new();
    public List<Guid> BetaStudents { get; } = new();

    public async Task SeedAsync(bool includeBeta = false)
    {
        var now = DateTime.UtcNow;
        SeedTenant(TenantAlpha, SchoolAlpha, ClassAlpha, TeacherAlpha, AlphaStudents, "Alpha", now);
        if (includeBeta)
        {
            SeedTenant(TenantBeta, SchoolBeta, ClassBeta, TeacherBeta, BetaStudents, "Beta", now);
        }
        await _db.SaveChangesAsync();
    }

    private void SeedTenant(Guid tenantId, Guid schoolTenantId, Guid classId, Guid teacherId, List<Guid> students, string prefix, DateTime now)
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

        for (var i = 0; i < 3; i++)
        {
            var studentId = Guid.NewGuid();
            students.Add(studentId);
            _db.StudentProfiles.Add(new StudentProfile
            {
                Id = studentId,
                TenantId = tenantId,
                DisplayName = $"{prefix}Student{i + 1}",
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

    public ExamCreationService BuildCreationService()
    {
        var exams = new ExamRepository(_db);
        var questions = new ExamQuestionRepository(_db);
        var assignments = new ExamAssignmentRepository(_db);
        var teachers = new TeacherRepository(_db);
        var classes = new ClassGroupRepository(_db);
        var trails = new GuardrailDecisionTrailStore(_db);
        var guardrail = new CustomQuestionGuardrailValidator(trails);
        return new ExamCreationService(exams, questions, assignments, teachers, classes, guardrail);
    }

    public ExamAutoGrader BuildAutoGrader(RecordingSessionEventOutbox outbox)
    {
        var submissions = new ExamSubmissionRepository(_db);
        var emitter = new ExamEventEmitter(outbox);
        return new ExamAutoGrader(submissions, emitter);
    }

    public ExamSubmissionRepository Submissions() => new(_db);
    public ExamQuestionRepository Questions() => new(_db);
    public ExamRepository Exams() => new(_db);

    public static ExamQuestionInput MultipleChoiceQuestion(string correctOptionId, decimal points = 1m)
    {
        var opts = JsonSerializer.Serialize(new object[]
        {
            new { option_id = "A", label_ar = "أ", label_en = "A" },
            new { option_id = "B", label_ar = "ب", label_en = "B" },
            new { option_id = "C", label_ar = "ج", label_en = "C" },
        }, JsonOptions);
        var correct = JsonSerializer.Serialize(new { option_id = correctOptionId }, JsonOptions);
        return new ExamQuestionInput(
            QuestionSource: "phase1_content",
            Phase1ContentId: Guid.NewGuid(),
            QuestionTextAr: "اختر الإجابة الصحيحة",
            QuestionTextEn: "Pick the correct answer",
            QuestionType: "multiple_choice",
            OptionsJson: opts,
            CorrectAnswerJson: correct,
            Points: points);
    }

    public static ExamQuestionInput TrueFalseQuestion(bool correctValue, decimal points = 1m)
    {
        var correct = JsonSerializer.Serialize(new { value = correctValue }, JsonOptions);
        return new ExamQuestionInput(
            QuestionSource: "phase1_content",
            Phase1ContentId: Guid.NewGuid(),
            QuestionTextAr: "صح أم خطأ",
            QuestionTextEn: "True or false",
            QuestionType: "true_false",
            OptionsJson: null,
            CorrectAnswerJson: correct,
            Points: points);
    }

    public static ExamQuestionInput FillInBlankQuestion(string expected, decimal points = 1m)
    {
        var correct = JsonSerializer.Serialize(new { value = expected, accepted = new[] { expected } }, JsonOptions);
        return new ExamQuestionInput(
            QuestionSource: "phase1_content",
            Phase1ContentId: Guid.NewGuid(),
            QuestionTextAr: "املأ الفراغ",
            QuestionTextEn: "Fill the blank",
            QuestionType: "fill_in_blank",
            OptionsJson: null,
            CorrectAnswerJson: correct,
            Points: points);
    }

    public static ExamQuestionInput CustomQuestion(string ar, string en, string correctOption = "A")
    {
        var opts = JsonSerializer.Serialize(new object[]
        {
            new { option_id = "A", label_ar = "أ", label_en = "A" },
            new { option_id = "B", label_ar = "ب", label_en = "B" },
        }, JsonOptions);
        var correct = JsonSerializer.Serialize(new { option_id = correctOption }, JsonOptions);
        return new ExamQuestionInput(
            QuestionSource: "custom",
            Phase1ContentId: null,
            QuestionTextAr: ar,
            QuestionTextEn: en,
            QuestionType: "multiple_choice",
            OptionsJson: opts,
            CorrectAnswerJson: correct,
            Points: 1m);
    }

    public string BuildAnswersJson(IReadOnlyList<(Guid QuestionId, object Answer)> answers)
    {
        var list = answers.Select(a => new { exam_question_id = a.QuestionId, answer = a.Answer }).ToArray();
        return JsonSerializer.Serialize(new { answers = list }, JsonOptions);
    }
}

/// <summary>
/// Test-only <see cref="ISessionEventOutboxWriter"/> that captures the
/// outbox rows the ExamEventEmitter would write, without hitting the DB
/// context. The Phase 4 mastery pipeline is verified elsewhere; these
/// recordings let the US6 tests assert that grading enqueues one
/// <c>exam_answered</c> event per submission.
/// </summary>
public sealed class RecordingSessionEventOutbox : ISessionEventOutboxWriter
{
    public sealed record CapturedEvent(
        SessionEventKind Kind,
        Guid TenantId,
        Guid StudentSessionId,
        Guid CorrelationId,
        object Payload,
        CurriculumScope Scope,
        string PlanTierSnapshot);

    private readonly List<CapturedEvent> _captured = new();
    public IReadOnlyList<CapturedEvent> Captured => _captured;

    public Task<Guid> EnqueueAsync(
        SessionEventKind kind,
        Guid tenantId,
        Guid studentSessionId,
        Guid correlationId,
        object payload,
        CurriculumScope scope,
        string planTierSnapshot,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        _captured.Add(new CapturedEvent(kind, tenantId, studentSessionId, correlationId, payload, scope, planTierSnapshot));
        return Task.FromResult(id);
    }
}
