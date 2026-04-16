using Muallimi.Domain.Shared;
using Pgvector;

namespace Muallimi.Domain.Curriculum;

public class QaCacheEntry
{
    public Guid EntryId { get; set; }
    public CurriculumType CurriculumType { get; set; }
    public Subject Subject { get; set; }
    public string Topic { get; set; } = string.Empty;
    public Grade Grade { get; set; }
    public TutorLanguage TutorLanguage { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public Vector? QuestionEmbedding { get; set; }
    public string AnswerText { get; set; } = string.Empty;

    /// <summary>JSON array of source chunk IDs.</summary>
    public string SourceChunkIds { get; set; } = "[]";

    public string ModelVersion { get; set; } = string.Empty;
    public QaCacheStatus ValidationStatus { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? LastReviewedAt { get; set; }

    public void FlagForRevalidation()
    {
        if (ValidationStatus != QaCacheStatus.Active)
            return;
        ValidationStatus = QaCacheStatus.FlaggedForRevalidation;
    }
}
