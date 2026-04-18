using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.Exams.ExamCreation;

/// <summary>
/// T126 (US6) — ExamCreationService.
///
/// Creates an exam + questions + class-group assignments in one unit of
/// work. Responsibilities:
///   • validate the creating actor (teacher OR school admin) belongs to
///     the tenant;
///   • assemble questions from Phase 1 content IDs or caller-supplied
///     custom payloads, with the custom path flowing through the Phase 2
///     guardrail chain stub (<see cref="ICustomQuestionGuardrailValidator"/>);
///   • write the <see cref="Exam"/>, its <see cref="ExamQuestion"/> rows,
///     and an <see cref="ExamAssignment"/> per target class;
///   • return a typed result the endpoint layer projects.
///
/// Schedule + state transitions are handled by <see cref="ExamStateMachine"/>;
/// this service only creates the <c>draft</c> row.
/// </summary>
public sealed record ExamQuestionInput(
    string QuestionSource,
    Guid? Phase1ContentId,
    string QuestionTextAr,
    string QuestionTextEn,
    string QuestionType,
    string? OptionsJson,
    string CorrectAnswerJson,
    decimal Points);

public sealed record ExamCreationInput(
    Guid TenantId,
    Guid SchoolTenantId,
    Guid? CreatedByTeacherId,
    Guid? CreatedByAdminId,
    string TitleAr,
    string TitleEn,
    Guid SubjectId,
    int Grade,
    IReadOnlyList<string> TopicBindings,
    int? DurationMinutes,
    DateTime? ScheduledStart,
    DateTime? ScheduledEnd,
    IReadOnlyList<ExamQuestionInput> Questions,
    IReadOnlyList<Guid> ClassGroupIds,
    string CorrelationId);

public sealed record ExamCreationResult(
    Exam Exam,
    IReadOnlyList<ExamQuestion> Questions,
    IReadOnlyList<ExamAssignment> Assignments);

public interface IExamCreationService
{
    Task<ExamCreationResult> CreateAsync(ExamCreationInput input, CancellationToken ct = default);
}

public sealed class ExamCreationService : IExamCreationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly HashSet<string> SupportedQuestionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "multiple_choice",
        "true_false",
        "fill_in_blank",
    };

    private readonly IExamRepository _exams;
    private readonly IExamQuestionRepository _questions;
    private readonly IExamAssignmentRepository _assignments;
    private readonly ITeacherRepository _teachers;
    private readonly IClassGroupRepository _classes;
    private readonly ICustomQuestionGuardrailValidator _guardrail;

    public ExamCreationService(
        IExamRepository exams,
        IExamQuestionRepository questions,
        IExamAssignmentRepository assignments,
        ITeacherRepository teachers,
        IClassGroupRepository classes,
        ICustomQuestionGuardrailValidator guardrail)
    {
        _exams = exams;
        _questions = questions;
        _assignments = assignments;
        _teachers = teachers;
        _classes = classes;
        _guardrail = guardrail;
    }

    public async Task<ExamCreationResult> CreateAsync(ExamCreationInput input, CancellationToken ct = default)
    {
        if (input.Questions.Count == 0)
            throw new InvalidOperationException("exam_requires_at_least_one_question");
        if (input.CreatedByTeacherId is null && input.CreatedByAdminId is null)
            throw new InvalidOperationException("exam_requires_creator");

        if (input.CreatedByTeacherId is Guid teacherId)
        {
            var teacher = await _teachers.GetByIdAsync(input.TenantId, input.SchoolTenantId, teacherId, ct)
                ?? throw new InvalidOperationException("teacher_not_found");
            if (teacher.DeactivatedAt is not null)
                throw new InvalidOperationException("teacher_deactivated");
        }

        foreach (var cid in input.ClassGroupIds.Distinct())
        {
            var c = await _classes.GetByIdAsync(input.TenantId, input.SchoolTenantId, cid, ct)
                ?? throw new InvalidOperationException("class_not_found");
            if (c.SchoolTenantId != input.SchoolTenantId)
                throw new InvalidOperationException("class_cross_tenant");
        }

        var now = DateTime.UtcNow;
        var exam = new Exam
        {
            ExamId = Guid.NewGuid(),
            TenantId = input.TenantId,
            SchoolTenantId = input.SchoolTenantId,
            CreatedByTeacherId = input.CreatedByTeacherId,
            CreatedByAdminId = input.CreatedByAdminId,
            TitleAr = input.TitleAr,
            TitleEn = input.TitleEn,
            SubjectId = input.SubjectId,
            Grade = input.Grade,
            TopicBindings = JsonSerializer.Serialize(input.TopicBindings, JsonOptions),
            ScheduledStart = input.ScheduledStart,
            ScheduledEnd = input.ScheduledEnd,
            DurationMinutes = input.DurationMinutes,
            Status = ExamStates.Draft,
            TotalPoints = 0m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var questionRows = new List<ExamQuestion>(input.Questions.Count);
        var order = 1;
        decimal total = 0m;
        foreach (var q in input.Questions)
        {
            if (!SupportedQuestionTypes.Contains(q.QuestionType))
                throw new InvalidOperationException("unsupported_question_type");
            if (q.Points <= 0m)
                throw new InvalidOperationException("question_points_must_be_positive");

            var row = new ExamQuestion
            {
                ExamQuestionId = Guid.NewGuid(),
                TenantId = input.TenantId,
                ExamId = exam.ExamId,
                QuestionSource = string.IsNullOrWhiteSpace(q.QuestionSource) ? "phase1_content" : q.QuestionSource,
                Phase1ContentId = q.Phase1ContentId,
                QuestionTextAr = q.QuestionTextAr ?? string.Empty,
                QuestionTextEn = q.QuestionTextEn ?? string.Empty,
                QuestionType = q.QuestionType,
                Options = q.OptionsJson,
                CorrectAnswer = string.IsNullOrWhiteSpace(q.CorrectAnswerJson) ? "{}" : q.CorrectAnswerJson,
                Points = q.Points,
                DisplayOrder = order++,
            };

            if (row.QuestionSource == "custom")
            {
                var validation = await _guardrail.ValidateAsync(
                    input.TenantId,
                    row.ExamQuestionId,
                    new CustomQuestionValidationInput(
                        row.QuestionTextAr,
                        row.QuestionTextEn,
                        row.QuestionType,
                        row.CorrectAnswer,
                        input.CorrelationId),
                    ct);
                if (!validation.Approved)
                {
                    throw new CustomQuestionRejectedException(validation.Violations);
                }
                row.GuardrailDecisionTrailId = validation.GuardrailDecisionTrailId;
            }

            questionRows.Add(row);
            total += row.Points;
        }

        exam.TotalPoints = total;

        await _exams.AddAsync(exam, ct);
        foreach (var q in questionRows)
        {
            await _questions.AddAsync(q, ct);
        }

        var assignments = new List<ExamAssignment>();
        foreach (var cid in input.ClassGroupIds.Distinct())
        {
            var a = new ExamAssignment
            {
                ExamAssignmentId = Guid.NewGuid(),
                TenantId = input.TenantId,
                ExamId = exam.ExamId,
                ClassGroupId = cid,
                AssignedAt = now,
            };
            await _assignments.AddAsync(a, ct);
            assignments.Add(a);
        }

        await _exams.SaveChangesAsync(ct);

        return new ExamCreationResult(exam, questionRows, assignments);
    }
}

public sealed class CustomQuestionRejectedException : InvalidOperationException
{
    public IReadOnlyList<string> Violations { get; }

    public CustomQuestionRejectedException(IReadOnlyList<string> violations)
        : base("custom_question_rejected")
    {
        Violations = violations;
    }
}

public static class ExamCreationServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ExamCreationService(this IServiceCollection services)
    {
        services.AddScoped<IExamCreationService, ExamCreationService>();
        return services;
    }
}
