using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Muallimi.Api.RetrievalApi;

/// <summary>
/// T094 - Q&amp;A cache lookup using semantic similarity ≥ 0.88,
/// scoped by curriculum type and subject.
/// Only returns entries with validation_status = Active or PreSeeded.
/// </summary>
public class QaCacheLookupService
{
    public const double MinSimilarityThreshold = 0.88;

    private readonly MuallimiDbContext _db;

    public QaCacheLookupService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<QaCacheHit?> LookupAsync(
        Vector queryEmbedding,
        CurriculumType curriculumType,
        Subject subject,
        CancellationToken ct = default)
    {
        var hit = await _db.QaCacheEntries
            .Where(e => e.CurriculumType == curriculumType
                        && e.Subject == subject
                        && (e.ValidationStatus == QaCacheStatus.Active
                            || e.ValidationStatus == QaCacheStatus.PreSeeded)
                        && e.QuestionEmbedding != null)
            .OrderBy(e => e.QuestionEmbedding!.CosineDistance(queryEmbedding))
            .Select(e => new QaCacheHit
            {
                EntryId = e.EntryId,
                QuestionText = e.QuestionText,
                AnswerText = e.AnswerText,
                CosineDistance = e.QuestionEmbedding!.CosineDistance(queryEmbedding)
            })
            .FirstOrDefaultAsync(ct);

        if (hit is null) return null;

        hit.Similarity = 1.0 - hit.CosineDistance;

        // Only return if similarity meets threshold
        return hit.Similarity >= MinSimilarityThreshold ? hit : null;
    }
}

public class QaCacheHit
{
    public Guid EntryId { get; set; }
    public string QuestionText { get; set; } = string.Empty;
    public string AnswerText { get; set; } = string.Empty;
    public double CosineDistance { get; set; }
    public double Similarity { get; set; }
}
