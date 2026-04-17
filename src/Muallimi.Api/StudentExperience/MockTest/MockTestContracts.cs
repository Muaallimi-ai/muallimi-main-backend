using System;
using System.Collections.Generic;
using Muallimi.Api.StudentExperience.QuizDelivery;

namespace Muallimi.Api.StudentExperience.MockTest;

/// <summary>
/// T094 / T096 (US6) — Wire DTOs for the Phase 3 Mock Test surface.
/// Shapes follow
/// <c>specs/005-student-learning-experience/contracts/quiz-and-mock-test-contract.md</c>
/// and are serialised snake_case via the pipeline-wide JSON naming policy.
///
/// The question payload reuses <see cref="QuizQuestionPayload"/> from the
/// Solve Questions surface so the mock-test player renders the same
/// question card component. Correctness is never leaked on the wire during
/// a run — only the submit response exposes per-question correctness as
/// part of the final score card.
/// </summary>
public sealed record MockTestStartRequest(
    Guid SessionId,
    Guid SubjectId,
    IReadOnlyList<Guid>? ChapterIds,
    int TimeLimitSeconds,
    int? QuestionCount);

public sealed record MockTestStartResponse(
    Guid MockTestSessionId,
    DateTime ServerStartedAt,
    DateTime ServerDeadlineAt,
    int QuestionBankSnapshotSize,
    QuizQuestionPayload FirstQuestion,
    string PlanGate);

public sealed record MockTestAnswerRequest(
    string QuestionId,
    string ChosenOptionId,
    bool IsFlagged);

public sealed record MockTestStateResponse(
    Guid MockTestSessionId,
    DateTime ServerNow,
    DateTime ServerDeadlineAt,
    int SecondsRemaining,
    IReadOnlyList<MockTestProgressPayload> Progress,
    QuizQuestionPayload? CurrentQuestion,
    string State);

public sealed record MockTestProgressPayload(
    string QuestionId,
    string? ChosenOptionId,
    bool IsFlagged);

public sealed record MockTestSubmitResponse(
    Guid MockTestSessionId,
    string State,
    MockTestFinalScorePayload FinalScore,
    IReadOnlyList<MockTestTopicBreakdownPayload> PerTopicBreakdown);

public sealed record MockTestFinalScorePayload(
    int Correct,
    int Total,
    double Percent);

public sealed record MockTestTopicBreakdownPayload(
    string TopicId,
    int Correct,
    int Total);
