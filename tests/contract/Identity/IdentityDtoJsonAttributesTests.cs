using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// Guardrail contract test: every public property on every Identity DTO
/// MUST carry a <see cref="JsonPropertyName"/> attribute.
///
/// Background: main-backend's global <c>ConfigureHttpJsonOptions</c>
/// policy is <c>SnakeCaseLower</c>, applied to every minimal-API
/// request body and response payload. The Identity module (auth,
/// account management, children, user profile) intentionally pins
/// camelCase on the wire — that contract is published as
/// <c>specs/009-identity-auth/contracts/identity-http-contract.md</c>
/// and frozen by the legacy-AuthAPI cutover flow.
///
/// A new Identity DTO shipped without <c>[JsonPropertyName]</c>
/// would silently flip to snake_case on the wire, breaking the
/// frontend's envelope + auth payload shapes. This test fails fast
/// in CI the moment a developer forgets an attribute.
///
/// Scope: every class + record declared under the
/// <c>Muallimi.Application.Identity.Dtos</c> namespace. Command records
/// (<c>Muallimi.Application.Identity.Commands</c>) are NOT wire types
/// and are excluded — the public endpoints bind into <c>Request</c>
/// DTOs and map those into commands internally.
/// </summary>
public class IdentityDtoJsonAttributesTests
{
    private const string DtoNamespace = "Muallimi.Application.Identity.Dtos";

    [Fact]
    public void Every_Identity_Dto_Property_Has_JsonPropertyName()
    {
        var assembly = typeof(Muallimi.Application.Identity.Dtos.ApiResponseEnvelope<object>).Assembly;
        var dtoTypes = assembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith(DtoNamespace, StringComparison.Ordinal))
            .Where(t => t.IsClass && !t.IsAbstract)
            // Skip compiler-generated helpers (e.g. `<>c__DisplayClass*`).
            .Where(t => !t.Name.Contains("<"))
            .ToArray();

        Assert.NotEmpty(dtoTypes);

        var missing = new List<string>();
        foreach (var type in dtoTypes)
        {
            foreach (var prop in GetPublicInstanceProperties(type))
            {
                // Records generated from primary constructors back their
                // parameters with a compiler-synthesised `EqualityContract`
                // property that has no setter and is not a wire field.
                if (prop.Name == "EqualityContract") continue;
                // Only inspect readable properties — wire types don't
                // expose write-only members.
                if (!prop.CanRead) continue;

                var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
                if (attr is null)
                {
                    missing.Add($"{type.FullName}.{prop.Name}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Identity DTO properties missing [JsonPropertyName]:\n  - {string.Join("\n  - ", missing)}");
    }

    [Fact]
    public void Every_Identity_Dto_JsonPropertyName_Is_CamelCase()
    {
        var assembly = typeof(Muallimi.Application.Identity.Dtos.ApiResponseEnvelope<object>).Assembly;
        var dtoTypes = assembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith(DtoNamespace, StringComparison.Ordinal))
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => !t.Name.Contains("<"))
            .ToArray();

        var violations = new List<string>();
        foreach (var type in dtoTypes)
        {
            foreach (var prop in GetPublicInstanceProperties(type))
            {
                if (prop.Name == "EqualityContract") continue;
                var attr = prop.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
                if (attr is null) continue;
                if (!IsCamelCase(attr.Name))
                {
                    violations.Add($"{type.FullName}.{prop.Name} → \"{attr.Name}\"");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Identity DTO [JsonPropertyName] values must be camelCase (Phase 9 identity-http-contract §1):\n  - {string.Join("\n  - ", violations)}");
    }

    private static IEnumerable<PropertyInfo> GetPublicInstanceProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Concat(type.BaseType is null or { FullName: "System.Object" }
                ? Array.Empty<PropertyInfo>()
                : GetPublicInstanceProperties(type.BaseType));

    private static bool IsCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsLower(s[0])) return false;
        foreach (var c in s)
        {
            if (c == '_' || c == '-') return false;
        }
        return true;
    }
}
