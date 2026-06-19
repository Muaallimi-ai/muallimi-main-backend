using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Curriculum;

public class CurriculumSource
{
    public Guid SourceId { get; private set; }
    public CurriculumType CurriculumType { get; private set; }
    public Grade Grade { get; private set; }
    public Subject Subject { get; private set; }
    public string AcademicYear { get; private set; } = string.Empty;
    public TutorLanguage TutorLanguage { get; private set; }
    public FileFormat FileFormat { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string? OriginalFileName { get; private set; }
    public string UploadActor { get; private set; } = string.Empty;
    public DateTime UploadedAt { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public SourceStatus Status { get; private set; }

    private CurriculumSource() { } // EF Core

    public static CurriculumSource Create(
        CurriculumType curriculumType,
        Grade grade,
        Subject subject,
        string academicYear,
        TutorLanguage tutorLanguage,
        FileFormat fileFormat,
        string storageKey,
        string contentHash,
        string uploadActor,
        string? originalFileName = null)
    {
        if (string.IsNullOrWhiteSpace(academicYear))
            throw new ArgumentException("Academic year is required.", nameof(academicYear));
        if (string.IsNullOrWhiteSpace(storageKey))
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        if (string.IsNullOrWhiteSpace(contentHash))
            throw new ArgumentException("Content hash is required.", nameof(contentHash));
        if (string.IsNullOrWhiteSpace(uploadActor))
            throw new ArgumentException("Upload actor is required.", nameof(uploadActor));

        // MVP scope enforcement
        if (grade != Grade.Grade7)
            throw new InvalidOperationException("MVP scope is limited to Grade 7.");

        return new CurriculumSource
        {
            SourceId = Guid.NewGuid(),
            CurriculumType = curriculumType,
            Grade = grade,
            Subject = subject,
            AcademicYear = academicYear,
            TutorLanguage = tutorLanguage,
            FileFormat = fileFormat,
            StorageKey = storageKey,
            OriginalFileName = string.IsNullOrWhiteSpace(originalFileName) ? null : originalFileName.Trim(),
            ContentHash = contentHash,
            UploadActor = uploadActor,
            UploadedAt = DateTime.UtcNow,
            Status = SourceStatus.Received
        };
    }

    private static readonly HashSet<FileFormat> AllowedFormats = [FileFormat.Pdf, FileFormat.Docx, FileFormat.Html];

    public static void ValidateFormat(FileFormat format)
    {
        if (!AllowedFormats.Contains(format))
            throw new InvalidOperationException($"Unsupported file format '{format}'. Accepted formats: PDF, DOCX, HTML.");
    }

    public void MarkIngesting()
    {
        if (Status != SourceStatus.Received)
            throw new InvalidOperationException($"Cannot start ingesting from status '{Status}'. Must be '{SourceStatus.Received}'.");
        Status = SourceStatus.Ingesting;
    }

    public void MarkExtracted()
    {
        if (Status != SourceStatus.Ingesting)
            throw new InvalidOperationException($"Cannot mark extracted from status '{Status}'. Must be '{SourceStatus.Ingesting}'.");
        Status = SourceStatus.Extracted;
    }

    public void MarkInReview()
    {
        if (Status != SourceStatus.Extracted)
            throw new InvalidOperationException($"Cannot mark in-review from status '{Status}'. Must be '{SourceStatus.Extracted}'.");
        Status = SourceStatus.InReview;
    }

    public void MarkApproved()
    {
        if (Status != SourceStatus.InReview && Status != SourceStatus.Extracted)
            throw new InvalidOperationException($"Cannot mark approved from status '{Status}'. Must be '{SourceStatus.InReview}' or '{SourceStatus.Extracted}'.");
        Status = SourceStatus.Approved;
    }

    /// <summary>
    /// Resets the source back to <see cref="SourceStatus.Received"/> so the
    /// admin can re-trigger extraction. Allowed from any post-upload state
    /// except Indexed/Replaced (those are downstream of the pipeline being
    /// completed end-to-end and should not be casually reset).
    /// </summary>
    public void ResetForReextract()
    {
        if (Status == SourceStatus.Indexed || Status == SourceStatus.Replaced)
            throw new InvalidOperationException($"Cannot reset for re-extract from status '{Status}'.");
        Status = SourceStatus.Received;
    }

    public void MarkIndexed()
    {
        // Reached from Ingesting (legacy auto-pipeline), Extracted (no-review
        // staged flow), Approved (the future chunk+embed pass), or InReview
        // (operator skip-to-publish path).
        if (Status != SourceStatus.Ingesting && Status != SourceStatus.Extracted
            && Status != SourceStatus.Approved && Status != SourceStatus.InReview)
            throw new InvalidOperationException($"Cannot mark indexed from status '{Status}'.");
        Status = SourceStatus.Indexed;
    }

    public void MarkFailed(string reason)
    {
        if (Status != SourceStatus.Ingesting && Status != SourceStatus.Extracted
            && Status != SourceStatus.InReview)
            throw new InvalidOperationException($"Cannot mark failed from status '{Status}'.");
        Status = SourceStatus.Failed;
    }

    public void MarkReplaced()
    {
        if (Status != SourceStatus.Indexed)
            throw new InvalidOperationException($"Cannot mark replaced from status '{Status}'. Must be '{SourceStatus.Indexed}'.");
        Status = SourceStatus.Replaced;
    }
}
