using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Tenancy;

namespace Muallimi.Api.TutorExposure;

/// <summary>
/// T035 (US1) — Student-facing tutor endpoint facade. Enforces tenant + session
/// scope (<see cref="TutorAuthorizationFilter"/>), propagates the correlation
/// ID, and forwards to ai-service /internal/tutor/ask. The main-backend never
/// generates an answer itself — it only enforces the boundary and forwards.
/// </summary>
public static class TutorEndpoint
{
    private const string AiServiceBaseUrlKey = "AiService:BaseUrl";
    private const string AiServiceHttpClientName = "ai-service";
    public const string TutorAskRoute = "/api/tutor/ask";

    public static void MapTutorExposureEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(TutorAskRoute, async (HttpContext http, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var tenantId = http.Request.Headers["X-Tenant-Id"].FirstOrDefault();
            var sessionId = http.Request.Headers["X-Session-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(sessionId))
                return Results.Unauthorized();

            var correlationId = http.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString("N");

            using var body = new StreamReader(http.Request.Body);
            var payload = await body.ReadToEndAsync(ct);

            var client = factory.CreateClient(AiServiceHttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/tutor/ask")
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Correlation-Id", correlationId);
            request.Headers.Add("X-Tenant-Id", tenantId);
            request.Headers.Add("X-Session-Id", sessionId);

            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.Headers["X-Correlation-Id"] = correlationId;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await http.Response.WriteAsync(responseBody, ct);
            return Results.Empty;
        })
        .WithName("TutorAskFacade")
        .WithTags("Tutor");
    }

    public static void AddTutorExposureClient(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient(AiServiceHttpClientName, client =>
        {
            var baseUrl = config[AiServiceBaseUrlKey] ?? "http://localhost:5081";
            client.BaseAddress = new Uri(baseUrl);
        });
    }
}
