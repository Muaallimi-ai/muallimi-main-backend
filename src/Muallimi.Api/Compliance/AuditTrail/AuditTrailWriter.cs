using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.AuditTrail;

/// <summary>
/// T014 — Append-only audit writer. PII fields in the payload are masked before
/// insert. AuditEntry rows are never updated or deleted at the application layer.
/// </summary>
public class AuditTrailWriter
{
    private readonly MuallimiDbContext _db;

    public AuditTrailWriter(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task WriteAsync(AuditTrailEntry entry, CancellationToken ct = default)
    {
        var maskedPayload = entry.Payload is null
            ? null
            : PiiMasker.MaskPayload(entry.Payload);

        _db.AuditEntries.Add(new AuditEntry
        {
            AuditEntryId = Guid.NewGuid(),
            TenantId = entry.TenantId,
            ActorId = entry.ActorId,
            ActorType = entry.ActorType,
            TargetId = entry.TargetId,
            TargetType = entry.TargetType,
            ActionType = entry.ActionType,
            Payload = maskedPayload,
            IpAddress = entry.IpAddress,
            UserAgent = entry.UserAgent,
            CorrelationId = entry.CorrelationId,
            OccurredAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }
}

public record AuditTrailEntry
{
    public required Guid TenantId { get; init; }
    public required Guid ActorId { get; init; }
    public required string ActorType { get; init; }
    public Guid? TargetId { get; init; }
    public string? TargetType { get; init; }
    public required string ActionType { get; init; }
    public object? Payload { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public required string CorrelationId { get; init; }
}

internal static class PiiMasker
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "email", "phone", "ip_address", "ipAddress", "payment_token", "paymentToken",
        "student_name", "studentName", "parent_email", "parent_phone"
    };

    public static string MaskPayload(object payload)
    {
        var json = payload is string s ? s : JsonSerializer.Serialize(payload);
        try
        {
            var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                Mask(doc.RootElement, writer);
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json;
        }
    }

    private static void Mask(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if (SensitiveKeys.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue("***");
                    }
                    else
                    {
                        Mask(prop.Value, writer);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Mask(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
