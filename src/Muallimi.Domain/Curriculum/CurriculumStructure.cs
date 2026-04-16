namespace Muallimi.Domain.Curriculum;

public class CurriculumStructure
{
    public Guid StructureId { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>
    /// JSON-serialised ordered tree of nodes with node_id, parent_id, node_type, title, order, source_refs.
    /// </summary>
    public string Nodes { get; private set; } = "[]";

    public DateTime ExtractedAt { get; private set; }

    // Navigation
    public CurriculumSource? Source { get; set; }

    private CurriculumStructure() { } // EF Core

    public static CurriculumStructure Create(Guid sourceId, string nodesJson)
    {
        if (sourceId == Guid.Empty)
            throw new ArgumentException("Source ID is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(nodesJson))
            throw new ArgumentException("Nodes JSON is required.", nameof(nodesJson));

        return new CurriculumStructure
        {
            StructureId = Guid.NewGuid(),
            SourceId = sourceId,
            Nodes = nodesJson,
            ExtractedAt = DateTime.UtcNow
        };
    }

    public void UpdateNodes(string nodesJson)
    {
        if (string.IsNullOrWhiteSpace(nodesJson))
            throw new ArgumentException("Nodes JSON is required.", nameof(nodesJson));
        Nodes = nodesJson;
        ExtractedAt = DateTime.UtcNow;
    }
}
