using System;
using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.QuizDelivery;

/// <summary>
/// T083 / T085 (US5) — Wire DTOs for the Phase 3 Solve Questions surface.
/// Shapes follow
/// <c>specs/005-student-learning-experience/contracts/quiz-and-mock-test-contract.md</c>
/// and are serialised snake_case via the pipeline-wide JSON naming policy.
///
/// The question payload intentionally omits the correct-option id —
/// correctness is revealed only by the answer response so the client cannot
/// score locally before the facade records the attempt and emits
/// <c>quiz_answered</c>.
/// </summary>
public sealed record SolveQuestionsStartRequest(
    Guid SessionId,
    Guid SubjectId,
    Guid? ChapterId,
    Guid? TopicId,
    int QuestionCount);

public sealed record SolveQuestionsStartResponse(
    Guid QuizSessionId,
    int QuestionBankSnapshotSize,
    QuizQuestionPayload FirstQuestion);

public sealed record QuizQuestionPayload(
    string QuestionId,
    string StemTextAr,
    string StemTextEn,
    IReadOnlyList<QuizOptionPayload> Options);

public sealed record QuizOptionPayload(
    string OptionId,
    string TextAr,
    string TextEn);

public sealed record SolveQuestionsAnswerRequest(
    Guid QuizSessionId,
    string QuestionId,
    string ChosenOptionId);

public sealed record SolveQuestionsAnswerResponse(
    bool IsCorrect,
    string CorrectOptionId,
    string ExplanationTextAr,
    string ExplanationTextEn,
    QuizQuestionPayload? NextQuestion,
    bool QuizComplete);
