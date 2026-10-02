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

public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions.OpenAiOptions _opts;
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;

    public OpenAiEmbeddingProvider(
        HttpClient http,
        IOptions<EmbeddingOptions> options,
        ILogger<OpenAiEmbeddingProvider> logger)
    {
        _http = http;
        _opts = options.Value.OpenAI;
        _logger = logger;
    }

    public string ProviderKey => "openai";
    public string ModelName => _opts.Model;
    public int Dim => _opts.Dim;

    public async Task<EmbeddingResult?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("Embedding:OpenAI:ApiKey is not configured.");

        var req = new HttpRequestMessage(HttpMethod.Post, _opts.Endpoint)
        {
            Content = JsonContent.Create(new OpenAiRequest
            {
                Input = text,
                Model = _opts.Model,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI embed failed status={Status} body={Body}", resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        var parsed = JsonSerializer.Deserialize<OpenAiResponse>(body)
            ?? throw new InvalidOperationException("OpenAI returned empty response.");
        if (parsed.Data is null || parsed.Data.Length == 0 || parsed.Data[0].Embedding is null)
            throw new InvalidOperationException("OpenAI returned no embedding.");

        var vec = parsed.Data[0].Embedding!;
        if (vec.Length != _opts.Dim)
            _logger.LogWarning("OpenAI returned dim={Got} but configured Embedding:OpenAI:Dim={Expected}", vec.Length, _opts.Dim);

        return new EmbeddingResult(vec, ProviderKey, parsed.Model ?? _opts.Model, vec.Length);
    }

    private sealed class OpenAiRequest
    {
        [JsonPropertyName("input")] public string? Input { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
    }

    private sealed class OpenAiResponse
    {
        [JsonPropertyName("data")] public OpenAiDatum[]? Data { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
    }

    private sealed class OpenAiDatum
    {
        [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
    }
}
