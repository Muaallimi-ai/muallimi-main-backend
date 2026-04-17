using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.Tenancy;

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T015 — Phase 3 wrapper around the Phase 2 <c>ai.tutor.runtime</c>
/// contract. The facade never generates answers itself; it only forwards
/// the student request to ai-service (Phase 2) with tenant, session, and
/// correlation headers intact. This client is shared by the text-chat SSE
/// endpoint (T060) and the voice-chat endpoint (T073).
///
/// The underlying HttpClient is configured through DI; the DelegatingHandler
/// <see cref="CorrelationIdPropagationHandler"/> takes care of forwarding
/// <c>X-Correlation-Id</c>, <c>X-Tenant-Id</c>, and <c>X-Session-Id</c>.
/// </summary>
public interface ITutorRuntimeClient
{
    Task<HttpResponseMessage> AskAsync(Stream requestBody, string contentType, CancellationToken ct = default);
    Task<HttpResponseMessage> StreamAskAsync(Stream requestBody, string contentType, CancellationToken ct = default);
    Task<HttpResponseMessage> SynthesizeVoiceAsync(Stream requestBody, string contentType, CancellationToken ct = default);
}

public sealed class TutorRuntimeClient : ITutorRuntimeClient
{
    public const string HttpClientName = "phase3-tutor-runtime";
    private const string AskPath = "/internal/tutor/ask";
    private const string StreamPath = "/internal/tutor/ask/stream";
    private const string VoicePath = "/internal/tutor/voice";

    private readonly HttpClient _http;

    public TutorRuntimeClient(HttpClient http)
    {
        _http = http;
    }

    public Task<HttpResponseMessage> AskAsync(Stream body, string contentType, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, AskPath, body, contentType, ct);

    public Task<HttpResponseMessage> StreamAskAsync(Stream body, string contentType, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, StreamPath, body, contentType, ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead);

    public Task<HttpResponseMessage> SynthesizeVoiceAsync(Stream body, string contentType, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, VoicePath, body, contentType, ct,
            completionOption: HttpCompletionOption.ResponseHeadersRead);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, Stream body, string contentType, CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StreamContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return _http.SendAsync(request, completionOption, ct);
    }
}

public static class TutorRuntimeClientServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3TutorRuntimeClient(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient<ITutorRuntimeClient, TutorRuntimeClient>(TutorRuntimeClient.HttpClientName,
            client =>
            {
                client.BaseAddress = new Uri(config["AiService:BaseUrl"] ?? "http://localhost:5081");
            })
            .AddHttpMessageHandler<CorrelationIdPropagationHandler>();
        return services;
    }
}
