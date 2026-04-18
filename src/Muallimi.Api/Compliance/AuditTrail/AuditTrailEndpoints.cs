using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.AiOperations;
using Muallimi.Api.Tenancy;

namespace Muallimi.Api.Compliance.AuditTrail;

/// <summary>
/// T115 — Operator audit trail query and export endpoints per
/// audit-trail-contract.md. All routes require an operator/incident-investigation
/// role; rows are never mutated — this is a read-only surface plus export.
/// </summary>
public static class AuditTrailEndpoints
{
    public static IEndpointRouteBuilder MapAuditTrailEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/operator/audit-trail", ListAsync);
        routes.MapGet("/api/v1/operator/audit-trail/{auditEntryId:guid}", GetAsync);
        routes.MapPost("/api/v1/operator/audit-trail/export", CreateExportAsync);
        routes.MapGet("/api/v1/operator/audit-trail/export/{exportRequestId:guid}", DownloadExportAsync);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        AuditTrailQueryService queryService,
        Guid? tenant_id,
        Guid? actor_id,
        Guid? target_id,
        string? action_type,
        string? from,
        string? to,
        string? cursor,
        int? limit,
        string? locale,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var loc = locale == "en" ? "en" : "ar";

        var query = new AuditTrailQuery
        {
            TenantId = tenant_id,
            ActorId = actor_id,
            TargetId = target_id,
            ActionTypes = ParseActionTypes(action_type),
            From = ParseIso(from),
            To = ParseIso(to),
            Cursor = AuditTrailCursor.TryDecode(cursor),
            Limit = limit,
        };

        var result = await queryService.QueryAsync(query, ct);

        var entries = result.Entries.Select(a => new
        {
            audit_entry_id = a.AuditEntryId,
            tenant_id = a.TenantId,
            actor = new
            {
                actor_id = a.ActorId,
                actor_type = a.ActorType,
                display_name = ResolveActorDisplayName(a.ActorType, loc),
            },
            target = new
            {
                target_id = a.TargetId,
                target_type = a.TargetType,
                display_name = a.TargetType is null ? null : ResolveTargetDisplayName(a.TargetType, loc),
            },
            action_type = a.ActionType,
            action_label = AuditActionLabels.Resolve(a.ActionType, loc),
            payload_summary = Summarise(a.Payload),
            correlation_id = a.CorrelationId,
            occurred_at = a.OccurredAt,
        });

        return Results.Ok(new
        {
            entries,
            total_count = result.TotalCountEstimate,
            next_cursor = result.NextCursor?.Encode(),
        });
    }

    private static async Task<IResult> GetAsync(
        Guid auditEntryId,
        HttpContext http,
        AuditTrailQueryService queryService,
        string? locale,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out var role, out var forbid)) return forbid!;
        var loc = locale == "en" ? "en" : "ar";
        var entry = await queryService.GetByIdAsync(auditEntryId, ct);
        if (entry is null) return Results.NotFound();

        var fullPayload = role == AiOperationsAuthorizationFilter.OperatorRole ? entry.Payload : Summarise(entry.Payload);

        return Results.Ok(new
        {
            audit_entry_id = entry.AuditEntryId,
            tenant_id = entry.TenantId,
            actor = new
            {
                actor_id = entry.ActorId,
                actor_type = entry.ActorType,
                display_name = ResolveActorDisplayName(entry.ActorType, loc),
            },
            target = new
            {
                target_id = entry.TargetId,
                target_type = entry.TargetType,
                display_name = entry.TargetType is null ? null : ResolveTargetDisplayName(entry.TargetType, loc),
            },
            action_type = entry.ActionType,
            action_label = AuditActionLabels.Resolve(entry.ActionType, loc),
            payload = fullPayload,
            ip_address = MaskIp(entry.IpAddress),
            user_agent = entry.UserAgent,
            correlation_id = entry.CorrelationId,
            occurred_at = entry.OccurredAt,
        });
    }

    private static async Task<IResult> CreateExportAsync(
        HttpContext http,
        AuditTrailExportService exportService,
        AuditTrailExportInput input,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        if (input.From > input.To) return Results.BadRequest(new { error = "invalid_range" });

        var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        Guid.TryParse(http.Request.Headers["X-Actor-Id"].FirstOrDefault(), out var requestedBy);

        var bundle = await exportService.GenerateAsync(
            new AuditTrailExportRequest(input.TenantId, input.From, input.To, input.ActionTypes, input.Format),
            requestedBy, correlationId, ct);

        return Results.Ok(new
        {
            export_request_id = bundle.ExportRequestId,
            status = "processing",
            estimated_completion = bundle.GeneratedAt,
            entry_count = bundle.EntryCount,
        });
    }

    private static IResult DownloadExportAsync(
        Guid exportRequestId,
        HttpContext http,
        AuditTrailExportStore store)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        if (!store.TryGet(exportRequestId, out var bundle) || bundle is null)
            return Results.NotFound();
        return Results.File(bundle.Bytes, bundle.ContentType, bundle.FileName);
    }

    private static IReadOnlyCollection<string>? ParseActionTypes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static DateTime? ParseIso(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt
            : null;
    }

    private static string ResolveActorDisplayName(string actorType, string locale)
    {
        return actorType switch
        {
            "student" => locale == "ar" ? "طالب" : "Student",
            "parent" => locale == "ar" ? "ولي أمر" : "Parent",
            "teacher" => locale == "ar" ? "معلّم" : "Teacher",
            "school_admin" => locale == "ar" ? "مشرف مدرسة" : "School Admin",
            "operator" => locale == "ar" ? "مُشغّل" : "Operator",
            "system" => locale == "ar" ? "النظام" : "System",
            _ => actorType,
        };
    }

    private static string ResolveTargetDisplayName(string targetType, string locale)
    {
        return targetType switch
        {
            "tenant" => locale == "ar" ? "مستأجر" : "Tenant",
            "subscription" => locale == "ar" ? "اشتراك" : "Subscription",
            "invoice" => locale == "ar" ? "فاتورة" : "Invoice",
            "payment_transaction" => locale == "ar" ? "معاملة دفع" : "Payment Transaction",
            "student" => locale == "ar" ? "طالب" : "Student",
            "audit_trail" => locale == "ar" ? "سجل التدقيق" : "Audit Trail",
            _ => targetType,
        };
    }

    private static string? Summarise(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        const int maxLength = 160;
        return payload.Length <= maxLength ? payload : payload[..maxLength] + "…";
    }

    private static string? MaskIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var parts = ip.Split('.');
        if (parts.Length == 4)
        {
            return $"{parts[0]}.{parts[1]}.{parts[2]}.***";
        }
        return "***";
    }
}

public sealed record AuditTrailExportInput(
    Guid? TenantId,
    DateTime From,
    DateTime To,
    IReadOnlyCollection<string>? ActionTypes,
    string? Format);
