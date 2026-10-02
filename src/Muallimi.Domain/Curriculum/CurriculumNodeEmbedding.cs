using System;
using Pgvector;

namespace Muallimi.Domain.Curriculum;

public class CurriculumNodeEmbedding
{
    public Guid NodeId { get; set; }
    public Guid SourceId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string RetrievalClass { get; set; } = string.Empty;

    public string EmbedBodySha256 { get; set; } = string.Empty;

    public string ProviderKey { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int Dim { get; set; }

    public Vector? VoyageEmbedding { get; set; }
    public Vector? OpenAiEmbedding { get; set; }
    public Vector? LocalEmbedding { get; set; }

    public DateTime EmbeddedAt { get; set; }
}
