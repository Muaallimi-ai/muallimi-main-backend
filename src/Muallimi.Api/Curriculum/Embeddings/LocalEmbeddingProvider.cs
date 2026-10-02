using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Muallimi.Api.Curriculum.Embeddings;

public sealed class LocalEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions.LocalOptions _opts;
    private readonly ILogger<LocalEmbeddingProvider> _logger;

    public LocalEmbeddingProvider(
        HttpClient http,
        IOptions<EmbeddingOptions> options,
        ILogger<LocalEmbeddingProvider> logger)
    {
        _http = http;
        _opts = options.Value.Local;
        _logger = logger;
    }

    public string ProviderKey => "local";
    public string ModelName => _opts.Model;
    public int Dim => _opts.Dim;

    public async Task<EmbeddingResult?> EmbedAsync(string text, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            _opts.Endpoint,
            new LocalRequest { Input = new[] { text }, Model = _opts.Model, InputType = _opts.InputType },
            ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Local embed failed status={Status} body={Body}", resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var parsed = JsonSerializer.Deserialize<LocalResponse>(body)
            ?? throw new InvalidOperationException("Local sidecar returned empty response.");
        if (parsed.Data is null || parsed.Data.Length == 0 || parsed.Data[0].Embedding is null)
            throw new InvalidOperationException("Local sidecar returned no embedding.");

        var vec = parsed.Data[0].Embedding!;
        if (vec.Length != _opts.Dim)
            throw new InvalidOperationException(
                $"Local sidecar returned dim={vec.Length} but local_embedding column is vector({_opts.Dim}).");

        return new EmbeddingResult(vec, ProviderKey, parsed.Model ?? _opts.Model, vec.Length);
    }

    private sealed class LocalRequest
    {
        [JsonPropertyName("input")] public string[]? Input { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("input_type")] public string? InputType { get; set; }
    }

    private sealed class LocalResponse
    {
        [JsonPropertyName("data")] public LocalDatum[]? Data { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
    }

    private sealed class LocalDatum
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}
