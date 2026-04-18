using System;
using System.Collections.Generic;

namespace Muallimi.Api.Exams.ExamCreation;

/// <summary>
/// T125 (US6) — Exam state machine.
///
/// The exam lifecycle contract (<c>phase5.exam.lifecycle</c>) fixes the
/// progression:
///
/// <code>
/// draft → scheduled → open → closed → graded → published
/// </code>
///
/// Transitions are monotonic: once an exam is <c>closed</c> it cannot
/// reopen, once <c>published</c> it is terminal. Attempts to move to an
/// unreachable status raise <see cref="InvalidExamStateTransitionException"/>
/// with a deterministic error code so contract tests and the frontend can
/// surface a consistent message.
///
/// The state machine is intentionally a pure function: no DB calls, no
/// services. Callers load the exam, call <see cref="Transition"/>, and
/// persist the returned status.
/// </summary>
public static class ExamStates
{
    public const string Draft = "draft";
    public const string Scheduled = "scheduled";
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Graded = "graded";
    public const string Published = "published";

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedTransitions
        = new Dictionary<string, IReadOnlySet<string>>
        {
            [Draft] = new HashSet<string> { Scheduled },
            [Scheduled] = new HashSet<string> { Scheduled, Open, Closed },
            [Open] = new HashSet<string> { Closed },
            [Closed] = new HashSet<string> { Graded },
            [Graded] = new HashSet<string> { Published },
            [Published] = new HashSet<string>(),
        };
}

public sealed class InvalidExamStateTransitionException : InvalidOperationException
{
    public string FromStatus { get; }
    public string ToStatus { get; }

    public InvalidExamStateTransitionException(string from, string to)
        : base($"invalid_exam_state_transition:{from}->{to}")
    {
        FromStatus = from;
        ToStatus = to;
    }
}

public static class ExamStateMachine
{
    public static string Transition(string currentStatus, string requestedStatus)
    {
        if (string.Equals(currentStatus, requestedStatus, StringComparison.Ordinal)
            && currentStatus == ExamStates.Scheduled)
        {
            return requestedStatus; // reschedule with same status is allowed
        }

        if (!ExamStates.AllowedTransitions.TryGetValue(currentStatus, out var allowed)
            || !allowed.Contains(requestedStatus))
        {
            throw new InvalidExamStateTransitionException(currentStatus, requestedStatus);
        }

        return requestedStatus;
    }

    public static bool CanOpen(string currentStatus, int questionCount)
        => currentStatus == ExamStates.Scheduled && questionCount > 0;
}
