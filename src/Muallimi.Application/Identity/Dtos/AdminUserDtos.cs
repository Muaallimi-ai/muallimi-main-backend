using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Muallimi.Application.Identity.Dtos;

/// <summary>
/// T110 — DTOs for the Phase 9 US3 admin surface.
/// </summary>
public sealed class AdminUserSummary
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("fullName")] public string FullName { get; init; } = string.Empty;
    [JsonPropertyName("fullNameEn")] public string? FullNameEn { get; init; }
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("tenantType")] public string TenantType { get; init; } = string.Empty;
    [JsonPropertyName("accountType")] public string AccountType { get; init; } = "personal";
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("locale")] public string Locale { get; init; } = "ar";
    [JsonPropertyName("roles")] public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    [JsonPropertyName("emailVerified")] public bool EmailVerified { get; init; }
    [JsonPropertyName("twoFactorEnabled")] public bool TwoFactorEnabled { get; init; }
    [JsonPropertyName("requiresPasswordReset")] public bool RequiresPasswordReset { get; init; }
    [JsonPropertyName("lastLoginAt")] public DateTime? LastLoginAt { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
}

public sealed class AdminUserList
{
    [JsonPropertyName("users")] public IReadOnlyList<AdminUserSummary> Users { get; init; } = Array.Empty<AdminUserSummary>();
    [JsonPropertyName("totalCount")] public int TotalCount { get; init; }
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("pageSize")] public int PageSize { get; init; }
}

public sealed class AdminUserDetail
{
    [JsonPropertyName("user")] public AdminUserSummary User { get; init; } = new();
    [JsonPropertyName("sessions")] public IReadOnlyList<AdminSessionSummary> Sessions { get; init; } = Array.Empty<AdminSessionSummary>();
    [JsonPropertyName("recentActivity")] public IReadOnlyList<AdminAuditEntry> RecentActivity { get; init; } = Array.Empty<AdminAuditEntry>();
}

public sealed class AdminSessionSummary
{
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("ipAddress")] public string? IpAddress { get; init; }
    [JsonPropertyName("userAgent")] public string? UserAgent { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("lastSeenAt")] public DateTime LastSeenAt { get; init; }
    [JsonPropertyName("revokedAt")] public DateTime? RevokedAt { get; init; }
}

public sealed class AdminRoleDescriptor
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("scope")] public string Scope { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("isSystem")] public bool IsSystem { get; init; }
}

public sealed class AdminInvitationResult
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; init; } = string.Empty;
    [JsonPropertyName("invitationLink")] public string InvitationLink { get; init; } = string.Empty;
    [JsonPropertyName("rolesGranted")] public IReadOnlyList<string> RolesGranted { get; init; } = Array.Empty<string>();
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; init; }
}

public sealed class AdminAuditEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("actorId")] public string ActorId { get; init; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; init; } = string.Empty;
    [JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
    [JsonPropertyName("targetType")] public string TargetType { get; init; } = string.Empty;
    [JsonPropertyName("targetId")] public string TargetId { get; init; } = string.Empty;
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; init; } = string.Empty;
    [JsonPropertyName("correlationId")] public string CorrelationId { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("occurredAt")] public DateTime OccurredAt { get; init; }
}

public sealed class AdminAuditPage
{
    [JsonPropertyName("entries")] public IReadOnlyList<AdminAuditEntry> Entries { get; init; } = Array.Empty<AdminAuditEntry>();
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; init; }
    [JsonPropertyName("totalCountEstimate")] public int TotalCountEstimate { get; init; }
}
