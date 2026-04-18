using Serilog.Core;
using Serilog.Events;

namespace Muallimi.Api.Observability.StructuredLogging;

/// <summary>
/// T019 — Enriches every log event with correlation_id, tenant_id, service_name,
/// and action fields. The correlation ID is read from the ambient HttpContext
/// (set by Phase 3 CorrelationPropagation middleware) when available.
/// </summary>
public class StructuredLoggingEnricher : ILogEventEnricher
{
    public const string ServiceName = "main-backend";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public StructuredLoggingEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("service_name", ServiceName));

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null) return;

        var correlationId = httpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? httpContext.TraceIdentifier;
        logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("correlation_id", correlationId));

        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(tenantId))
        {
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("tenant_id", tenantId));
        }
    }
}
