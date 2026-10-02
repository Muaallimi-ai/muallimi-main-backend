using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Muallimi.Api.Curriculum.Embeddings;

public sealed class VoyageEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions.VoyageOptions _opts;
    private readonly ILogger<VoyageEmbeddingProvider> _logger;

    public VoyageEmbeddingProvider(
        HttpClient http,
        IOptions<EmbeddingOptions> options,
        ILogger<VoyageEmbeddingProvider> logger)
    {
        _http = http;
        _opts = options.Value.Voyage;
        _logger = logger;
    }

    public string ProviderKey => "voyage";
    public string ModelName => _opts.Model;
    public int Dim => _opts.Dim;

    public async Task<EmbeddingResult?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("Embedding:Voyage:ApiKey is not configured.");

        var req = new HttpRequestMessage(HttpMethod.Post, _opts.Endpoint)
        {
            Content = JsonContent.Create(new VoyageRequest
            {
                Input = new[] { text },
                Model = _opts.Model,
                InputType = _opts.InputType,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Voyage embed failed status={Status} body={Body}", resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var parsed = JsonSerializer.Deserialize<VoyageResponse>(body)
            ?? throw new InvalidOperationException("Voyage returned empty response.");
        if (parsed.Data is null || parsed.Data.Length == 0 || parsed.Data[0].Embedding is null)
            throw new InvalidOperationException("Voyage returned no embedding.");

        var vec = parsed.Data[0].Embedding!;
        if (vec.Length != _opts.Dim)
            _logger.LogWarning("Voyage returned dim={Got} but configured Embedding:Voyage:Dim={Expected}", vec.Length, _opts.Dim);

        return new EmbeddingResult(vec, ProviderKey, parsed.Model ?? _opts.Model, vec.Length);
    }

    private sealed class VoyageRequest
    {
        [JsonPropertyName("input")] public string[]? Input { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("input_type")] public string? InputType { get; set; }
    }

    private sealed class VoyageResponse
    {
        [JsonPropertyName("data")] public VoyageDatum[]? Data { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
    }

    private sealed class VoyageDatum
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}
