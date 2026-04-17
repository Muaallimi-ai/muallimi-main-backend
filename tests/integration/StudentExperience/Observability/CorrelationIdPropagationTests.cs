using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.Tenancy;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.Observability;

/// <summary>
/// T127 — Correlation ID propagation contract across frontend → main-backend
/// → ai-service → document-ingestion.
///
/// The client (<c>Muaallimi-Platform/src/app/(student)/_lib/correlationId.ts</c>)
/// mints a UUID v4 per logical student request and sets the
/// <c>X-Correlation-Id</c> header. The main-backend preserves it (the
/// existing <c>CorrelationIdMiddleware</c> stashes it into
/// <c>HttpContext.Items["CorrelationId"]</c>) and the Phase 3
/// <see cref="CorrelationIdPropagationHandler"/> attaches it to every
/// outbound HttpClient call to ai-service and document-ingestion.
///
/// These tests pin down the handler end of the chain, because that is the
/// piece unique to Phase 3. The inbound middleware is already covered by
/// CorrelationIdEndToEndTests. Here we assert:
///
///   - An incoming request's <c>X-Correlation-Id</c> flows onto the
///     outbound ai-service/document-ingestion request verbatim.
///   - If the inbound request omits the header, the handler falls back to a
///     freshly minted v4 GUID (no null/empty propagation).
///   - <c>X-Tenant-Id</c> and <c>X-Session-Id</c> headers propagate only
///     when present — never fabricated.
///   - The handler never overwrites a value the caller already attached.
/// </summary>
public class CorrelationIdPropagationTests
{
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string TenantHeader = "X-Tenant-Id";
    private const string SessionHeader = "X-Session-Id";

    [Fact]
    public async Task Inbound_CorrelationId_Flows_To_Outbound_AiService_Call()
    {
        var incomingCorrelation = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid().ToString();

        var (client, recorder) = BuildClient(incomingCorrelation, tenantId, sessionId: null);

        var response = await client.GetAsync("https://ai-service.local/tutor/runtime/answer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(incomingCorrelation, recorder.Headers[CorrelationHeader]);
        Assert.Equal(tenantId, recorder.Headers[TenantHeader]);
        Assert.False(recorder.Headers.ContainsKey(SessionHeader));
    }

    [Fact]
    public async Task Missing_Inbound_CorrelationId_Falls_Back_To_New_Guid()
    {
        // An anonymous /healthz probe doesn't carry the header. The handler
        // MUST fabricate a correlation id rather than sending an empty
        // value, so downstream logs still line up.
        var (client, recorder) = BuildClient(
            incomingCorrelationId: null, tenantId: null, sessionId: null);

        await client.GetAsync("https://document-ingestion.local/retrieval/lookup");

        Assert.True(recorder.Headers.ContainsKey(CorrelationHeader));
        var outbound = recorder.Headers[CorrelationHeader];
        Assert.False(string.IsNullOrWhiteSpace(outbound));
        Assert.True(Guid.TryParseExact(outbound, "N", out _) || Guid.TryParse(outbound, out _),
            $"outbound correlation header is not a GUID: {outbound}");
    }

    [Fact]
    public async Task Tenant_And_Session_Headers_Only_Propagate_When_Present()
    {
        var correlation = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid().ToString();
        var sessionIdValue = Guid.NewGuid().ToString();

        var (client, recorder) = BuildClient(correlation, tenant, sessionIdValue);

        await client.GetAsync("https://ai-service.local/tutor/runtime/answer");

        Assert.Equal(correlation, recorder.Headers[CorrelationHeader]);
        Assert.Equal(tenant, recorder.Headers[TenantHeader]);
        Assert.Equal(sessionIdValue, recorder.Headers[SessionHeader]);
    }

    [Fact]
    public async Task Handler_Does_Not_Overwrite_A_Correlation_Header_The_Caller_Already_Attached()
    {
        // If a caller has already pinned an outbound correlation id (e.g.
        // batching a retry with the original id), the handler must not
        // replace it with the current request's id. This matters for the
        // session-event dispatcher, which publishes events from a
        // background service where there is no HttpContext.
        var requestCorrelation = Guid.NewGuid().ToString();
        var outboundCorrelation = Guid.NewGuid().ToString();

        var (client, recorder) = BuildClient(requestCorrelation, tenantId: null, sessionId: null);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://ai-service.local/tutor/runtime/answer");
        req.Headers.Add(CorrelationHeader, outboundCorrelation);
        await client.SendAsync(req);

        Assert.Equal(outboundCorrelation, recorder.Headers[CorrelationHeader]);
    }

    private static (HttpClient client, OutboundRecorder recorder) BuildClient(
        string? incomingCorrelationId, string? tenantId, string? sessionId)
    {
        var httpContext = new DefaultHttpContext();
        if (!string.IsNullOrEmpty(incomingCorrelationId))
        {
            httpContext.Request.Headers[CorrelationHeader] = incomingCorrelationId;
            httpContext.Items["CorrelationId"] = incomingCorrelationId;
        }
        if (!string.IsNullOrEmpty(tenantId))
        {
            httpContext.Request.Headers[TenantHeader] = tenantId;
        }
        if (!string.IsNullOrEmpty(sessionId))
        {
            httpContext.Request.Headers[SessionHeader] = sessionId;
        }

        var accessor = new FixedHttpContextAccessor(httpContext);
        var recorder = new OutboundRecorder();
        var handler = new CorrelationIdPropagationHandler(accessor)
        {
            InnerHandler = recorder,
        };
        return (new HttpClient(handler), recorder);
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public FixedHttpContextAccessor(HttpContext context) => HttpContext = context;
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class OutboundRecorder : HttpMessageHandler
    {
        public System.Collections.Generic.Dictionary<string, string> Headers { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var h in request.Headers)
            {
                Headers[h.Key] = string.Join(",", h.Value);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
