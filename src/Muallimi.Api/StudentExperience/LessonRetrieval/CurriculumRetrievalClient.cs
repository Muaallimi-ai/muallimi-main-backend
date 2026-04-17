using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.Tenancy;

namespace Muallimi.Api.StudentExperience.LessonRetrieval;

/// <summary>
/// T016 — Phase 3 wrapper around the Phase 1 <c>curriculum.runtime.retrieval</c>
/// contract. Used by Study mode (T047), Solve Questions (T083), Mock Test
/// (T094), and Homework Help (T105) to read published lessons, chunks, and
/// the question bank filtered by tenant + curriculum type + grade + subject.
///
/// Lookup-only. Writes go through the Phase 1 publication pipeline, never
/// through this client.
/// </summary>
public interface ICurriculumRetrievalClient
{
    Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct = default);
    Task<HttpResponseMessage> PostAsync(string path, HttpContent content, CancellationToken ct = default);
}

public sealed class CurriculumRetrievalClient : ICurriculumRetrievalClient
{
    public const string HttpClientName = "phase3-curriculum-retrieval";

    private readonly HttpClient _http;

    public CurriculumRetrievalClient(HttpClient http)
    {
        _http = http;
    }

    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct = default)
        => _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);

    public Task<HttpResponseMessage> PostAsync(string path, HttpContent content, CancellationToken ct = default)
        => _http.PostAsync(path, content, ct);
}

public static class CurriculumRetrievalClientServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3CurriculumRetrievalClient(
        this IServiceCollection services, IConfiguration config)
    {
        // In Phase 3 the retrieval surface is the *local* main-backend
        // Retrieval API that already consumes Phase 1 lookup-only tables.
        // The base URL is therefore the main-backend itself by default.
        services.AddHttpClient<ICurriculumRetrievalClient, CurriculumRetrievalClient>(
            CurriculumRetrievalClient.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(
                    config["CurriculumRetrieval:BaseUrl"] ?? "http://localhost:5080");
            })
            .AddHttpMessageHandler<CorrelationIdPropagationHandler>();
        return services;
    }
}
