using System;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Records a conflict or failure encountered during the legacy AuthAPI backfill.
/// Written by <c>BackfillScriptRunner</c> when a row cannot be migrated cleanly.
/// </summary>
public class BackfillError
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? LegacyUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
