using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Curriculum;

/// <summary>
/// Audit record of a lesson update, asset invalidation, replacement, or deprecation.
/// Every invalidation and replacement of a published asset must produce a matching entry.
/// </summary>
public class ChangeLogEntry
{
    public Guid EntryId { get; private set; }
    public Guid LessonId { get; private set; }
    public ChangeEventType EventType { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTime OccurredAt { get; private set; }
    public string? CorrelationId { get; private set; }

    private ChangeLogEntry() { } // EF Core

    public static ChangeLogEntry Create(
        Guid lessonId,
        ChangeEventType eventType,
        string actorId,
        string reason,
        string? correlationId = null)
    {
        if (lessonId == Guid.Empty)
            throw new ArgumentException("Lesson ID is required.", nameof(lessonId));
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor ID is required.", nameof(actorId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Reason is required.", nameof(reason));

        return new ChangeLogEntry
        {
            EntryId = Guid.NewGuid(),
            LessonId = lessonId,
            EventType = eventType,
            ActorId = actorId,
            Reason = reason,
            OccurredAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }
}
