using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.StudentExperience.Tenancy;

/// <summary>
/// T008 — Phase 3 correlation ID propagation.
///
/// The existing <c>Muallimi.Api.Audit.CorrelationIdMiddleware</c> already
/// accepts/issues the <c>X-Correlation-Id</c> header on inbound requests and
/// stashes it in <c>HttpContext.Items["CorrelationId"]</c>.
///
/// This file adds the outbound half: a <see cref="DelegatingHandler"/> that
/// attaches the current request's correlation ID (plus <c>X-Tenant-Id</c> and
/// <c>X-Session-Id</c> if present) to every ai-service and
/// document-ingestion HttpClient call, so the ID flows end-to-end across the
/// four repositories.
/// </summary>
public sealed class CorrelationIdPropagationHandler : DelegatingHandler
{
    public const string CorrelationIdHeader = "X-Correlation-Id";
    public const string TenantIdHeader = "X-Tenant-Id";
    public const string SessionIdHeader = "X-Session-Id";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdPropagationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is not null)
        {
            PropagateFromItemsOrHeader(request, ctx, CorrelationIdHeader, "CorrelationId",
                fallback: () => Guid.NewGuid().ToString("N"));
            PropagateFromHeader(request, ctx, TenantIdHeader);
            PropagateFromHeader(request, ctx, SessionIdHeader);
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static void PropagateFromItemsOrHeader(
        HttpRequestMessage request, HttpContext ctx, string headerName,
        string itemsKey, Func<string> fallback)
    {
        if (request.Headers.Contains(headerName)) return;
        var fromItems = ctx.Items[itemsKey]?.ToString();
        var fromHeader = ctx.Request.Headers[headerName].ToString();
        var value = !string.IsNullOrWhiteSpace(fromItems)
            ? fromItems
            : !string.IsNullOrWhiteSpace(fromHeader) ? fromHeader : fallback();
        request.Headers.Add(headerName, value);
    }

    private static void PropagateFromHeader(
        HttpRequestMessage request, HttpContext ctx, string headerName)
    {
        if (request.Headers.Contains(headerName)) return;
        var value = ctx.Request.Headers[headerName].ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.Add(headerName, value);
        }
    }
}

public static class CorrelationIdPropagationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CorrelationIdPropagationHandler"/> as a
    /// transient DelegatingHandler so it can be chained onto any
    /// <c>AddHttpClient</c> call via <c>AddHttpMessageHandler</c>.
    /// </summary>
    public static IServiceCollection AddPhase3CorrelationPropagation(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddTransient<CorrelationIdPropagationHandler>();
        return services;
    }
}
