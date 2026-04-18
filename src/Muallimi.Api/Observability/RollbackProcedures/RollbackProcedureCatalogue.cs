namespace Muallimi.Api.Observability.RollbackProcedures;

/// <summary>
/// T086 — Programmatic catalogue of the rollback procedures documented in
/// <c>RollbackProcedures.md</c>. Incident records can reference entries
/// here via <c>RunbookReference</c> for consistent dashboard links.
/// </summary>
public static class RollbackProcedureCatalogue
{
    public const string DocumentRelativePath = "Observability/RollbackProcedures/RollbackProcedures.md";

    public static readonly IReadOnlyList<RollbackProcedure> All = new[]
    {
        new RollbackProcedure(
            Unit: "main-backend",
            Title: "muallimi-main-backend rollback",
            Anchor: "#1-muallimi-main-backend",
            MaxWindowHours: 72,
            QueueCompatibilityDays: 90),
        new RollbackProcedure(
            Unit: "ai-service",
            Title: "muallimi-ai-service rollback",
            Anchor: "#2-muallimi-ai-service",
            MaxWindowHours: 48,
            QueueCompatibilityDays: 0),
        new RollbackProcedure(
            Unit: "document-ingestion",
            Title: "muallimi-document-ingestion rollback",
            Anchor: "#3-muallimi-document-ingestion",
            MaxWindowHours: 24 * 30,
            QueueCompatibilityDays: 30),
        new RollbackProcedure(
            Unit: "frontend",
            Title: "Muaallimi-Platform (frontend) rollback",
            Anchor: "#4-muaallimi-platform-frontend",
            MaxWindowHours: 24 * 14,
            QueueCompatibilityDays: 0),
    };

    public static RollbackProcedure? Find(string unit)
        => All.FirstOrDefault(p => string.Equals(p.Unit, unit, StringComparison.OrdinalIgnoreCase));
}

public sealed record RollbackProcedure(
    string Unit,
    string Title,
    string Anchor,
    int MaxWindowHours,
    int QueueCompatibilityDays);
