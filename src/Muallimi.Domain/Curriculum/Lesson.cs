using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Curriculum;

public class Lesson
{
    public Guid LessonId { get; private set; }
    public Guid StructureId { get; private set; }
    public CurriculumType CurriculumType { get; private set; }
    public Grade Grade { get; private set; }
    public Subject Subject { get; private set; }
    public TutorLanguage TutorLanguage { get; private set; }
    public string Path { get; private set; } = string.Empty;
    public string ContentHash { get; private set; } = string.Empty;
    public LessonStatus Status { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public string? LastChangeReason { get; private set; }

    // Navigation
    public CurriculumStructure? Structure { get; set; }

    private Lesson() { } // EF Core

    public static Lesson Create(
        Guid structureId,
        CurriculumType curriculumType,
        Grade grade,
        Subject subject,
        TutorLanguage tutorLanguage,
        string path)
    {
        if (structureId == Guid.Empty)
            throw new ArgumentException("Structure ID is required.", nameof(structureId));
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Lesson path is required.", nameof(path));

        return new Lesson
        {
            LessonId = Guid.NewGuid(),
            StructureId = structureId,
            CurriculumType = curriculumType,
            Grade = grade,
            Subject = subject,
            TutorLanguage = tutorLanguage,
            Path = path,
            Status = LessonStatus.New
        };
    }

    public void SetContentHash(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            throw new ArgumentException("Content hash is required.", nameof(contentHash));
        ContentHash = contentHash;
    }

    /// <summary>Returns true if the content hash changed, false if identical.</summary>
    public bool UpdateContentHash(string newHash)
    {
        if (string.IsNullOrWhiteSpace(newHash))
            throw new ArgumentException("Content hash is required.", nameof(newHash));
        if (ContentHash == newHash) return false;
        ContentHash = newHash;
        return true;
    }

    public void MarkIngested(string reason = "Ingestion completed")
    {
        if (Status != LessonStatus.New && Status != LessonStatus.Invalidated)
            throw new InvalidOperationException($"Cannot mark ingested from status '{Status}'.");
        Status = LessonStatus.Ingested;
        LastChangeReason = reason;
    }

    public void MarkGenerating()
    {
        if (Status != LessonStatus.Ingested)
            throw new InvalidOperationException($"Cannot start generating from status '{Status}'.");
        Status = LessonStatus.Generating;
    }

    public void MarkAutoValidating()
    {
        if (Status != LessonStatus.Generating)
            throw new InvalidOperationException($"Cannot start auto-validating from status '{Status}'.");
        Status = LessonStatus.AutoValidating;
    }

    public void MarkPendingAdminReview()
    {
        if (Status != LessonStatus.AutoValidating)
            throw new InvalidOperationException($"Cannot move to admin review from status '{Status}'.");
        Status = LessonStatus.PendingAdminReview;
    }

    public void MarkPendingExpertReview()
    {
        if (Status != LessonStatus.PendingAdminReview)
            throw new InvalidOperationException($"Cannot move to expert review from status '{Status}'.");
        Status = LessonStatus.PendingExpertReview;
    }

    public void MarkApproved()
    {
        if (Status != LessonStatus.PendingExpertReview)
            throw new InvalidOperationException($"Cannot approve from status '{Status}'.");
        Status = LessonStatus.Approved;
        PublishedAt = DateTime.UtcNow;
        LastChangeReason = "All assets approved";
    }

    public void MarkRejected(string reason)
    {
        if (Status != LessonStatus.PendingAdminReview && Status != LessonStatus.PendingExpertReview)
            throw new InvalidOperationException($"Cannot reject from status '{Status}'.");
        Status = LessonStatus.Rejected;
        LastChangeReason = reason;
    }

    public void Invalidate(string reason)
    {
        if (Status != LessonStatus.Approved)
            throw new InvalidOperationException($"Cannot invalidate from status '{Status}'.");
        Status = LessonStatus.Invalidated;
        PublishedAt = null;
        LastChangeReason = reason;
    }
}
