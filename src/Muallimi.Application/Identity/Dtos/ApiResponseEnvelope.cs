using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Muallimi.Application.Identity.Dtos;

/// <summary>
/// T048 — Standard response envelope preserved verbatim from the legacy
/// <c>Muaallimi-AuthAPI</c>. The frontend <c>auth.service.ts</c>
/// (T049, next session) cuts over by flipping a single environment
/// variable (<c>NEXT_PUBLIC_AUTH_API_URL</c>) — so the envelope shape
/// MUST stay byte-identical to what the legacy API emitted.
///
/// Contract-asserted by <c>ApiResponseEnvelopeShapeTests</c> (T056).
/// </summary>
public sealed class ApiResponseEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<ApiResponseError>? Errors { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    public static ApiResponseEnvelope<T> Ok(T data, string message, string correlationId)
        => new()
        {
            Success = true,
            Message = message,
            Data = data,
            Errors = null,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId,
        };

    public static ApiResponseEnvelope<T> Fail(
        string message,
        IReadOnlyList<ApiResponseError> errors,
        string correlationId)
        => new()
        {
            Success = false,
            Message = message,
            Data = default,
            Errors = errors,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId,
        };
}

public sealed class ApiResponseError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("field")]
    public string? Field { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
