using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.DataRetention;

/// <summary>
/// T119 — Ensures the default retention policies from
/// data-retention-contract.md exist on startup. Idempotent: missing rows are
/// inserted; existing rows are left untouched so operator adjustments survive
/// restarts.
/// </summary>
public static class DefaultRetentionPolicySeeder
{
    public static readonly IReadOnlyList<(string EntityType, int Days, string Rule)> Defaults = new[]
    {
        ("session_event",          365,  "anonymise"),
        ("ai_operations_metric",   180,  "delete"),
        ("notification_receipt",   90,   "delete"),
        ("audit_entry",            2555, "archive"),
        ("payment_transaction",    2555, "archive"),
        ("invoice",                2555, "archive"),
        ("dead_letter_message",    30,   "delete"),
        ("alert_event",            365,  "delete"),
        ("incident_record",        1095, "archive"),
    };

    public static async Task EnsureSeededAsync(MuallimiDbContext db, CancellationToken ct = default)
    {
        var existing = await db.DataRetentionPolicies
            .Select(p => p.EntityType)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var toAdd = new List<DataRetentionPolicy>();
        foreach (var (entityType, days, rule) in Defaults)
        {
            if (existingSet.Contains(entityType)) continue;
            toAdd.Add(new DataRetentionPolicy
            {
                PolicyId = Guid.NewGuid(),
                EntityType = entityType,
                RetentionDays = days,
                AnonymisationRule = rule,
                IsActive = true,
                CreatedByOperatorId = Guid.Empty,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        if (toAdd.Count > 0)
        {
            db.DataRetentionPolicies.AddRange(toAdd);
            await db.SaveChangesAsync(ct);
        }
    }
}
