using System;
using Microsoft.AspNetCore.Http;
using Muallimi.Application.Identity.Dtos;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// Shared helpers for Phase 9 auth endpoints — envelope wrapping,
/// correlation-id extraction, IP address harvesting. Intentionally
/// small so each individual endpoint handler stays readable.
/// </summary>
internal static class AuthEndpointHelpers
{
    public const string CorrelationHeader = "X-Correlation-Id";

    public static string ResolveCorrelationId(HttpContext http)
    {
        var raw = http.Request.Headers[CorrelationHeader].ToString();
        if (!string.IsNullOrWhiteSpace(raw)) return raw;
        var generated = Guid.NewGuid().ToString("D");
        http.Response.Headers[CorrelationHeader] = generated;
        return generated;
    }

    public static void EchoCorrelation(HttpContext http, string correlationId)
    {
        http.Response.Headers[CorrelationHeader] = correlationId;
    }

    public static string ResolveIp(HttpContext http)
    {
        var fwd = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.Split(',', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }
        return http.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
    }

    public static string? ResolveUserAgent(HttpContext http)
        => http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    public static IResult OkEnvelope<T>(T data, string message, string correlationId)
    {
        return Results.Ok(ApiResponseEnvelope<T>.Ok(data, message, correlationId));
    }

    public static IResult StatusEnvelope<T>(int status, T? data, string message, string correlationId)
    {
        var envelope = data is null
            ? new ApiResponseEnvelope<T>
            {
                Success = status is >= 200 and < 300,
                Message = message,
                Data = default,
                Errors = null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId,
            }
            : new ApiResponseEnvelope<T>
            {
                Success = status is >= 200 and < 300,
                Message = message,
                Data = data,
                Errors = null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId,
            };
        return Results.Json(envelope, statusCode: status);
    }

    public static IResult FailEnvelope(
        int status,
        string code,
        string message,
        string correlationId,
        System.Collections.Generic.IReadOnlyList<ApiResponseError>? errors = null)
    {
        var payload = ApiResponseEnvelope<object>.Fail(
            message,
            errors ?? new[] { new ApiResponseError { Code = code, Message = message } },
            correlationId);
        return Results.Json(payload, statusCode: status);
    }
}
