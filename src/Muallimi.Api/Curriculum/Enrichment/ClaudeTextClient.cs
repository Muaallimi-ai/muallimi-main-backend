using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Muallimi.Api.Curriculum.Enrichment;

public sealed class ClaudeOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-6";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public int MaxTokens { get; set; } = 2048;
}

public sealed class ClaudeTextClient
{
    private readonly HttpClient _http;
    private readonly ClaudeOptions _opts;
    private readonly ILogger<ClaudeTextClient> _logger;

    public ClaudeTextClient(HttpClient http, IOptions<ClaudeOptions> opts, ILogger<ClaudeTextClient> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_opts.BaseUrl))
            _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
    }

    public string Model => _opts.Model;

    public async Task<string> SendTextAsync(string prompt, int? maxTokensOverride, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            throw new InvalidOperationException("Anthropic:ApiKey is not configured on main-backend.");

        var payload = new
        {
            model = _opts.Model,
            max_tokens = maxTokensOverride ?? _opts.MaxTokens,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                    },
                },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.Add("x-api-key", _opts.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var requestId = resp.Headers.TryGetValues("request-id", out var ids) ? string.Join(',', ids) : string.Empty;

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Claude text request failed: {Status} request_id={RequestId} body={Body}",
                (int)resp.StatusCode, requestId, body);
            throw new InvalidOperationException(
                $"Claude call failed with status {(int)resp.StatusCode} (request_id={requestId}). Body: {body}");
        }

        var parsed = JsonSerializer.Deserialize<ClaudeMessageResponse>(body)
            ?? throw new InvalidOperationException("Claude returned unparseable response envelope.");
        var text = parsed.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        if (string.Equals(parsed.StopReason, "max_tokens", StringComparison.Ordinal))
            _logger.LogWarning("Claude hit max_tokens (len={Len}); response may be truncated.", text.Length);

        return text;
    }

    private sealed class ClaudeMessageResponse
    {
        [JsonPropertyName("content")] public ClaudeContentBlock[]? Content { get; set; }
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
    }

    private sealed class ClaudeContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
