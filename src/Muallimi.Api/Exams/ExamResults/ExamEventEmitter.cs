using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.Exams.ExamResults;

/// <summary>
/// T022 — <c>ExamEventEmitter</c>.
///
/// Emits <c>exam_answered</c> session events through the Phase 3 event
/// transport so the Phase 4 mastery pipeline (progress → mastery → streak →
/// badge) consumes exam results identically to quiz and mock-test answers.
/// The payload carries score, max_score, subject/topic scope, and
/// correlation id; the Phase 4 consumer (see
/// <c>Phase3EventConsumer</c> / <c>ProgressIngestionWorker</c>) treats
/// <c>exam_answered</c> like <c>quiz_answered</c> for mastery computation.
///
/// Callers invoke <see cref="EmitAsync"/> inside the same transaction that
/// marks the exam submission as <c>graded</c> so the outbox row commits
/// atomically with the grade.
/// </summary>
public interface IExamEventEmitter
{
    Task<Guid> EmitAsync(
        ExamSubmission submission,
        Guid studentSessionId,
        Guid subjectId,
        Guid? topicId,
        string planTierSnapshot,
        CancellationToken ct = default);
}

public sealed class ExamEventEmitter : IExamEventEmitter
{
    private readonly ISessionEventOutboxWriter _outbox;

    public ExamEventEmitter(ISessionEventOutboxWriter outbox)
    {
        _outbox = outbox;
    }

    public Task<Guid> EmitAsync(
        ExamSubmission submission,
        Guid studentSessionId,
        Guid subjectId,
        Guid? topicId,
        string planTierSnapshot,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(submission.CorrelationId, out var correlationId))
        {
            correlationId = Guid.NewGuid();
        }

        var payload = new
        {
            exam_id = submission.ExamId,
            submission_id = submission.ExamSubmissionId,
            student_id = submission.StudentId,
            score = submission.Score ?? 0m,
            max_score = submission.MaxScore,
            graded_at = submission.GradedAt ?? DateTime.UtcNow,
        };

        var scope = new CurriculumScope(
            CurriculumType: null,
            Grade: null,
            SubjectId: subjectId,
            ChapterId: null,
            TopicId: topicId,
            LessonId: null);

        return _outbox.EnqueueAsync(
            SessionEventKind.exam_answered,
            submission.TenantId,
            studentSessionId,
            correlationId,
            payload,
            scope,
            planTierSnapshot,
            ct);
    }
}

public static class ExamEventEmitterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ExamEventEmitter(this IServiceCollection services)
    {
        services.AddScoped<IExamEventEmitter, ExamEventEmitter>();
        return services;
    }
}
