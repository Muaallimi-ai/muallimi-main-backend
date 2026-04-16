using Muallimi.Domain.Shared;
using Pgvector;

namespace Muallimi.Domain.Curriculum;

public class ContentChunk
{
    public const int MinTokenCount = 300;
    public const int MaxTokenCount = 500;
    public const int OverlapTokens = 50;

    public Guid ChunkId { get; private set; }
    public Guid LessonId { get; private set; }
    public int Sequence { get; private set; }
    public string Text { get; private set; } = string.Empty;

    /// <summary>JSON array of structured equations (LaTeX + plain text).</summary>
    public string MathBlocks { get; private set; } = "[]";

    public int TokenCount { get; private set; }
    public int OverlapWithPrevious { get; private set; }

    /// <summary>JSON array of source document and page references.</summary>
    public string SourceRefs { get; private set; } = "[]";

    /// <summary>JSON object with curriculum_type, grade, subject, topic, subtopic, lesson, academic_year, tutor_language.</summary>
    public string Metadata { get; private set; } = "{}";

    public Vector? Embedding { get; private set; }
    public string? EmbeddingModelVersion { get; private set; }
    public ChunkStatus Status { get; private set; }

    // Navigation
    public Lesson? Lesson { get; set; }

    private ContentChunk() { } // EF Core

    public static ContentChunk Create(
        Guid lessonId,
        int sequence,
        string text,
        int tokenCount,
        int overlapWithPrevious,
        string sourceRefsJson,
        string metadataJson,
        string mathBlocksJson = "[]")
    {
        if (lessonId == Guid.Empty)
            throw new ArgumentException("Lesson ID is required.", nameof(lessonId));
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Chunk text is required.", nameof(text));

        return new ContentChunk
        {
            ChunkId = Guid.NewGuid(),
            LessonId = lessonId,
            Sequence = sequence,
            Text = text,
            MathBlocks = mathBlocksJson,
            TokenCount = tokenCount,
            OverlapWithPrevious = overlapWithPrevious,
            SourceRefs = sourceRefsJson,
            Metadata = metadataJson,
            Status = ChunkStatus.Draft
        };
    }

    public void SetEmbedding(float[] vector, string modelVersion)
    {
        if (vector.Length == 0)
            throw new ArgumentException("Embedding vector cannot be empty.", nameof(vector));
        if (string.IsNullOrWhiteSpace(modelVersion))
            throw new ArgumentException("Embedding model version is required.", nameof(modelVersion));

        Embedding = new Vector(vector);
        EmbeddingModelVersion = modelVersion;
    }

    public void Activate()
    {
        if (Status != ChunkStatus.Draft)
            throw new InvalidOperationException($"Cannot activate from status '{Status}'.");
        Status = ChunkStatus.Active;
    }

    public void Deprecate()
    {
        if (Status != ChunkStatus.Active)
            throw new InvalidOperationException($"Cannot deprecate from status '{Status}'.");
        Status = ChunkStatus.Deprecated;
    }
}
