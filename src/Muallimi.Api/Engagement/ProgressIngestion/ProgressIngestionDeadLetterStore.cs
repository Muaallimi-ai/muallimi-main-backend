using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.ProgressIngestion;

/// <summary>
/// T041 (US4) — Dead-letter store for progress ingestion.
///
/// Permanent rejection reasons (unknown tenant, unknown student, unknown
/// scope, malformed payload, unknown event kind) are recorded here with the
/// full envelope so an operator can replay or discard from the operations
/// surface. Transient failures (broker unavailable, DB lock contention)
/// stay on the broker queue with NACK-requeue and are NOT written here.
/// </summary>
public static class ProgressIngestionDeadLetterReasons
{
    public const string TenantNotFound = "tenant_not_found";
    public const string StudentNotFound = "student_not_found";
    public const string ScopeNotFound = "scope_not_found";
    public const string MalformedPayload = "malformed_payload";
    public const string UnknownEventKind = "unknown_event_kind";
}

public interface IProgressIngestionDeadLetterStore
{
    Task RecordAsync(Phase3EventEnvelope envelope, string reason, CancellationToken ct = default);
}

public sealed class ProgressIngestionDeadLetterStore : IProgressIngestionDeadLetterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public ProgressIngestionDeadLetterStore(MuallimiDbContext db) => _db = db;

    public async Task RecordAsync(Phase3EventEnvelope envelope, string reason, CancellationToken ct = default)
    {
        var row = new ProgressIngestionDeadLetter
        {
            DeadLetterId = Guid.NewGuid(),
            TenantId = envelope.TenantId == Guid.Empty ? null : envelope.TenantId,
            StudentId = envelope.StudentId == Guid.Empty ? null : envelope.StudentId,
            SourceEventId = envelope.SourceEventId ?? string.Empty,
            EventKind = envelope.EventKind ?? string.Empty,
            Reason = reason,
            Envelope = JsonSerializer.Serialize(envelope, JsonOptions),
            CorrelationId = envelope.CorrelationId ?? string.Empty,
            RecordedAt = DateTime.UtcNow,
        };
        _db.ProgressIngestionDeadLetters.Add(row);
        await _db.SaveChangesAsync(ct);
    }
}

public static class ProgressIngestionDeadLetterStoreServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ProgressIngestionDeadLetterStore(this IServiceCollection services)
    {
        services.AddScoped<IProgressIngestionDeadLetterStore, ProgressIngestionDeadLetterStore>();
        return services;
    }
}
