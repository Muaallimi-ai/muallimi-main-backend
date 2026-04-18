using System;
using System.Text.Json.Serialization;

namespace Muallimi.Api.Engagement.ProgressIngestion;

/// <summary>
/// Shared shape of a Phase 3 session event as published on the
/// <c>phase3.session.events</c> exchange. The Phase 3 writer side owns this
/// schema (see SessionEventOutboxWriter + SessionEventDispatcher); Phase 4
/// consumes it unchanged.
/// </summary>
public sealed class Phase3EventEnvelope
{
    [JsonPropertyName("source_event_id")]
    public string SourceEventId { get; set; } = string.Empty;

    [JsonPropertyName("event_kind")]
    public string EventKind { get; set; } = string.Empty;

    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; set; }

    [JsonPropertyName("student_id")]
    public Guid StudentId { get; set; }

    [JsonPropertyName("correlation_id")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("occurred_at")]
    public DateTime OccurredAt { get; set; }

    [JsonPropertyName("curriculum_scope")]
    public object? CurriculumScope { get; set; }

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}

public static class Phase3EventKinds
{
    public const string SessionStart = "session_start";
    public const string LessonView = "lesson_view";
    public const string ContentPlay = "content_play";
    public const string QuestionAsked = "question_asked";
    public const string AnswerReceived = "answer_received";
    public const string Refusal = "refusal";
    public const string QuizAnswered = "quiz_answered";
    public const string MockTest = "mock_test";
    public const string HomeworkHelpUsed = "homework_help_used";
    public const string WhiteboardSession = "whiteboard_session";
    public const string SessionEnd = "session_end";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        SessionStart, LessonView, ContentPlay, QuestionAsked, AnswerReceived,
        Refusal, QuizAnswered, MockTest, HomeworkHelpUsed, WhiteboardSession,
        SessionEnd,
    };
}
