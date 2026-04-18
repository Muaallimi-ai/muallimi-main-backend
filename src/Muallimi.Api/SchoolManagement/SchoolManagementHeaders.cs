using System;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.SchoolManagement;

/// <summary>
/// Shared header names for Phase 5 school management endpoints. Operator
/// endpoints require <c>X-Operator-Actor-Id</c> and <c>X-Tenant-Id</c>.
/// School-admin scoped endpoints require <c>X-Tenant-Id</c> and either
/// <c>X-School-Admin-Id</c> or a resolved <see cref="SchoolTenantProvisioning.SchoolTenantResolver"/>
/// claim (production) — the header path is the local-parity stub.
/// </summary>
internal static class SchoolManagementHeaders
{
    public const string TenantHeaderName = "X-Tenant-Id";
    public const string OperatorActorHeaderName = "X-Operator-Actor-Id";
    public const string SchoolAdminHeaderName = "X-School-Admin-Id";
    public const string SchoolTenantHeaderName = "X-School-Tenant-Id";
    public const string TeacherHeaderName = "X-Teacher-Id";
    public const string CorrelationHeaderName = "X-Correlation-Id";

    public static bool TryGetTenantId(HttpContext http, out Guid tenantId)
        => Guid.TryParse(http.Request.Headers[TenantHeaderName].ToString(), out tenantId);

    public static bool TryGetOperatorActorId(HttpContext http, out Guid operatorActorId)
        => Guid.TryParse(http.Request.Headers[OperatorActorHeaderName].ToString(), out operatorActorId);

    public static bool TryGetSchoolAdminId(HttpContext http, out Guid schoolAdminId)
        => Guid.TryParse(http.Request.Headers[SchoolAdminHeaderName].ToString(), out schoolAdminId);

    public static bool TryGetSchoolTenantId(HttpContext http, out Guid schoolTenantId)
        => Guid.TryParse(http.Request.Headers[SchoolTenantHeaderName].ToString(), out schoolTenantId);

    public static bool TryGetTeacherId(HttpContext http, out Guid teacherId)
        => Guid.TryParse(http.Request.Headers[TeacherHeaderName].ToString(), out teacherId);

    public static string ResolveCorrelationId(HttpContext http)
    {
        var raw = http.Request.Headers[CorrelationHeaderName].ToString();
        return string.IsNullOrWhiteSpace(raw) ? Guid.NewGuid().ToString("D") : raw;
    }
}
