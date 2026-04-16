using System.Text.Json;

namespace Muallimi.Domain.PromptAudit.Entities;

/// <summary>
/// T076 (US4) — Governed prompt record. Prompt metadata is mutable (purpose,
/// scope, active version pointer); prompt bodies and versions are not. Every
/// lifecycle transition (create, new version, promote, archive, rollback)
/// emits a <see cref="PromptAuditEntry"/>. Status drives runtime eligibility:
/// only a prompt whose active version is <c>Active</c> may be used to answer
/// a live request; <c>Archived</c> versions are refused loudly at runtime.
/// </summary>
public class Prompt
{
    public Guid PromptId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public Guid? ActiveVersionId { get; set; }
    public bool PromotionBlockFlag { get; set; }
    public string Status { get; set; } = PromptStatus.Draft;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static Prompt Create(string name, string purpose, string scope)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Prompt name is required.", nameof(name));

        var now = DateTime.UtcNow;
        return new Prompt
        {
            PromptId = Guid.NewGuid(),
            Name = name.Trim(),
            Purpose = purpose ?? string.Empty,
            Scope = scope ?? "global",
            ActiveVersionId = null,
            PromotionBlockFlag = false,
            Status = PromptStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void SetActiveVersion(Guid versionId)
    {
        if (Status == PromptStatus.Archived)
            throw new InvalidOperationException("Archived prompts cannot be promoted.");

        ActiveVersionId = versionId;
        Status = PromptStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Archive()
    {
        Status = PromptStatus.Archived;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// T114/T116 (US7) — Applied by
    /// <c>RedTeamResultPersistenceHandler</c> when a red-team run reports a
    /// regression whose <c>config_under_test</c> includes this prompt.
    /// Blocked prompts are rejected by <c>PromptRegistryEndpoints.Promote</c>
    /// until the flag is cleared by a subsequent passing run (FR-023).
    /// </summary>
    public void ApplyPromotionBlock()
    {
        PromotionBlockFlag = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ClearPromotionBlock()
    {
        PromotionBlockFlag = false;
        UpdatedAt = DateTime.UtcNow;
    }
}

public static class PromptStatus
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Archived = "archived";
}

public static class PromptVersionStatus
{
    public const string Draft = "draft";
    public const string Active = "active";
    public const string Archived = "archived";
}

/// <summary>
/// Immutable prompt body. Version numbers are monotonic per <see cref="PromptId"/>.
/// Once created the body never changes — promotions, archives, and rollbacks
/// only change the parent <see cref="Prompt.ActiveVersionId"/> pointer and the
/// version <see cref="Status"/>. Declared variables are persisted as a JSON
/// array of variable names; the ai-service variable validator fails loudly
/// when a render does not provide every declared variable (FR-013).
/// </summary>
public class PromptVersion
{
    public Guid VersionId { get; set; }
    public Guid PromptId { get; set; }
    public int VersionNumber { get; set; }
    public string Body { get; set; } = string.Empty;
    public string DeclaredVariables { get; set; } = "[]";
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = PromptVersionStatus.Draft;

    public static PromptVersion Create(
        Guid promptId,
        int versionNumber,
        string body,
        IEnumerable<string> declaredVariables,
        string createdBy)
    {
        if (versionNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(versionNumber), "Version numbers are monotonic and start at 1.");
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Prompt body is required.", nameof(body));
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new ArgumentException("change_actor is required.", nameof(createdBy));

        var declared = (declaredVariables ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new PromptVersion
        {
            VersionId = Guid.NewGuid(),
            PromptId = promptId,
            VersionNumber = versionNumber,
            Body = body,
            DeclaredVariables = JsonSerializer.Serialize(declared),
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            Status = PromptVersionStatus.Draft,
        };
    }

    public IReadOnlyList<string> ReadDeclaredVariables()
    {
        if (string.IsNullOrWhiteSpace(DeclaredVariables))
            return Array.Empty<string>();
        return JsonSerializer.Deserialize<string[]>(DeclaredVariables) ?? Array.Empty<string>();
    }

    public void MarkActive() => Status = PromptVersionStatus.Active;
    public void MarkSuperseded() => Status = PromptVersionStatus.Draft;
    public void MarkArchived() => Status = PromptVersionStatus.Archived;
}

/// <summary>
/// One audit entry per lifecycle action (FR-016). Populated by the write
/// endpoints in main-backend and surfaced verbatim in the prompt audit
/// timeline and in the incident-lookup trail for a given correlation ID.
/// </summary>
public class PromptAuditEntry
{
    public Guid EntryId { get; set; }
    public Guid PromptId { get; set; }
    public Guid? VersionId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? Diff { get; set; }

    public static PromptAuditEntry For(
        Guid promptId,
        Guid? versionId,
        string action,
        string actor,
        string? correlationId,
        string? diff = null)
    {
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Audit action is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("change_actor is required.", nameof(actor));

        return new PromptAuditEntry
        {
            EntryId = Guid.NewGuid(),
            PromptId = promptId,
            VersionId = versionId,
            Action = action,
            Actor = actor,
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow,
            Diff = diff,
        };
    }
}

public static class PromptAuditActions
{
    public const string Created = "created";
    public const string VersionCreated = "version_created";
    public const string Promoted = "promoted";
    public const string Archived = "archived";
    public const string RolledBack = "rolled_back";
}
