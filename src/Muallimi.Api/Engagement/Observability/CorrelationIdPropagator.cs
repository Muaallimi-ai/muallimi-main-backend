using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.Observability;

/// <summary>
/// T010 — Phase 4 correlation-ID propagator.
///
/// Every Phase 4 row derived from a Phase 3 session event carries the same
/// correlation identifier that was on the originating session event, so an
/// incident can be traced across phases without a join. The propagator
/// extracts the id from the inbound envelope (or falls back to generating a
/// new id if the inbound payload is malformed) and returns it in a canonical
/// string form that every downstream row stores.
/// </summary>
public interface ICorrelationIdPropagator
{
    string FromPhase3Envelope(IReadOnlyDictionary<string, object?> envelope);
    string FromJson(JsonElement element);
    string NewCorrelationId();
}

public sealed class CorrelationIdPropagator : ICorrelationIdPropagator
{
    public const string EnvelopeField = "correlation_id";

    public string FromPhase3Envelope(IReadOnlyDictionary<string, object?> envelope)
    {
        if (envelope.TryGetValue(EnvelopeField, out var raw) && raw is not null)
        {
            var value = raw.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return NewCorrelationId();
    }

    public string FromJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(EnvelopeField, out var raw) &&
            raw.ValueKind == JsonValueKind.String)
        {
            var value = raw.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }
        return NewCorrelationId();
    }

    public string NewCorrelationId() => Guid.NewGuid().ToString("D");
}

public static class CorrelationIdPropagatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4CorrelationIdPropagator(this IServiceCollection services)
    {
        services.AddSingleton<ICorrelationIdPropagator, CorrelationIdPropagator>();
        return services;
    }
}
