using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Muallimi.Application.Identity.Dtos;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// T056 — Contract test for the <c>ApiResponseEnvelope&lt;T&gt;</c> shape.
///
/// The Phase 9 backend replaces the legacy <c>Muaallimi-AuthAPI</c>, and
/// the frontend <c>auth.service.ts</c> cuts over by flipping
/// <c>NEXT_PUBLIC_AUTH_API_URL</c>. Any drift in property names, casing,
/// or ordering would break the frontend. The six envelope fields are
/// pinned here verbatim:
///   success, message, data, errors, timestamp, correlationId
/// and every error object carries:
///   code, field, message
/// </summary>
public class ApiResponseEnvelopeShapeTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    [Fact]
    public void Envelope_Exposes_Exactly_Six_Fields()
    {
        var names = JsonPropertyNames(typeof(ApiResponseEnvelope<object>));
        Assert.Equal(
            new[] { "success", "message", "data", "errors", "timestamp", "correlationId" }
                .OrderBy(x => x, StringComparer.Ordinal),
            names.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Error_Object_Exposes_Exactly_Three_Fields()
    {
        var names = JsonPropertyNames(typeof(ApiResponseError));
        Assert.Equal(
            new[] { "code", "field", "message" }.OrderBy(x => x, StringComparer.Ordinal),
            names.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Ok_Envelope_Serializes_With_Legacy_Shape()
    {
        var envelope = ApiResponseEnvelope<string>.Ok(
            data: "payload",
            message: "ok",
            correlationId: "corr-123");

        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("ok", root.GetProperty("message").GetString());
        Assert.Equal("payload", root.GetProperty("data").GetString());
        Assert.Equal("corr-123", root.GetProperty("correlationId").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("errors").ValueKind);
        Assert.True(root.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public void Fail_Envelope_Serializes_Errors_Array()
    {
        var envelope = ApiResponseEnvelope<object>.Fail(
            message: "validation_failed",
            errors: new List<ApiResponseError>
            {
                new() { Code = "email_required", Field = "email", Message = "Email is required" },
                new() { Code = "password_weak", Field = "password", Message = "Password too weak" },
            },
            correlationId: "corr-xyz");

        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);

        var errors = root.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.Equal(2, errors.GetArrayLength());

        var first = errors[0];
        Assert.Equal("email_required", first.GetProperty("code").GetString());
        Assert.Equal("email", first.GetProperty("field").GetString());
        Assert.Equal("Email is required", first.GetProperty("message").GetString());
    }

    [Fact]
    public void Envelope_Property_Casing_Is_Camel()
    {
        // The legacy API shipped camelCase (correlationId — not correlation_id).
        // We rely on explicit JsonPropertyName attributes — no
        // JsonNamingPolicy applied — so verify casing is preserved.
        var envelope = ApiResponseEnvelope<int>.Ok(42, "done", "abc");
        var json = JsonSerializer.Serialize(envelope, SerializerOptions);
        Assert.Contains("\"correlationId\":", json);
        Assert.DoesNotContain("correlation_id", json);
        Assert.DoesNotContain("\"CorrelationId\":", json);
    }

    private static string[] JsonPropertyNames(Type t)
    {
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? throw new InvalidOperationException(
                    $"{t.Name}.{p.Name} is missing [JsonPropertyName]"))
            .ToArray();
    }
}
