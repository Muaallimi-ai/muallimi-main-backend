using System;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// Shared header names for the parent-facing endpoints. Every parent
/// surface MUST send a tenant and parent profile id; operator
/// impersonation additionally sends an operator actor id and reason.
/// </summary>
internal static class ParentDashboardHeaders
{
    public const string TenantHeaderName = "X-Tenant-Id";
    public const string ParentProfileHeaderName = "X-Parent-Profile-Id";
    public const string CorrelationHeaderName = "X-Correlation-Id";
    public const string OperatorActorHeaderName = "X-Operator-Actor-Id";
    public const string OperatorReasonHeaderName = "X-Operator-Reason";

    public static bool TryGetTenantId(HttpContext http, out Guid tenantId)
        => Guid.TryParse(http.Request.Headers[TenantHeaderName].ToString(), out tenantId);

    public static bool TryGetParentProfileId(HttpContext http, out Guid parentProfileId)
        => Guid.TryParse(http.Request.Headers[ParentProfileHeaderName].ToString(), out parentProfileId);

    public static string ResolveCorrelationId(HttpContext http)
    {
        var raw = http.Request.Headers[CorrelationHeaderName].ToString();
        return string.IsNullOrWhiteSpace(raw) ? Guid.NewGuid().ToString("D") : raw;
    }

    public static bool TryGetOperatorContext(
        HttpContext http,
        out Guid operatorActorId,
        out string reason)
    {
        reason = http.Request.Headers[OperatorReasonHeaderName].ToString();
        var raw = http.Request.Headers[OperatorActorHeaderName].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            operatorActorId = Guid.Empty;
            return false;
        }
        return Guid.TryParse(raw, out operatorActorId);
    }
}
