using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T014 — Phase 4 wrapper around the Phase 1 retrieval contract.
///
/// Phase 4 calls retrieval read-only to:
///   - Resolve a focus-area deep link to a live Phase 1 curriculum node
///     (T112 deep-link validator).
///   - Resolve weekly report references back to Phase 1 chunks for the
///     evidence trail (T085).
///
/// The Phase 1 retrieval contract is consumed unchanged.
/// </summary>
public interface IPhase4CurriculumRetrievalClient
{
    Task<CurriculumNodeResolution> ResolveNodeAsync(
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        CancellationToken ct = default);
}

public sealed record CurriculumNodeResolution(
    bool Exists,
    string? Path,
    string? Status,
    IReadOnlyList<Guid> LessonIds);

public sealed class Phase4CurriculumRetrievalClient : IPhase4CurriculumRetrievalClient
{
    public const string HttpClientName = "phase4-curriculum-retrieval";

    private readonly HttpClient _http;

    public Phase4CurriculumRetrievalClient(HttpClient http)
    {
        _http = http;
    }

    public Task<CurriculumNodeResolution> ResolveNodeAsync(
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        CancellationToken ct = default)
    {
        // Full invocation is wired in T112 (focus-area deep-link validator)
        // and T085 (weekly report grounding). This foundational stub returns a
        // conservative negative so unwired surfaces fail safe.
        return Task.FromResult(new CurriculumNodeResolution(
            Exists: false,
            Path: null,
            Status: "pending",
            LessonIds: Array.Empty<Guid>()));
    }
}

public static class Phase4CurriculumRetrievalClientServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4CurriculumRetrievalClient(this IServiceCollection services)
    {
        services.AddHttpClient<IPhase4CurriculumRetrievalClient, Phase4CurriculumRetrievalClient>(
            Phase4CurriculumRetrievalClient.HttpClientName,
            (_, client) =>
            {
                client.BaseAddress = new System.Uri("http://localhost:5080/");
            });
        return services;
    }
}
