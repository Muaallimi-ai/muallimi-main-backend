using System.Text.Json;

namespace Muallimi.Api.Payments;

/// <summary>
/// Resolves the public backend URL for payment webhook callbacks.
///
/// In production: returns App:BackendBaseUrl from configuration.
/// In local dev with ngrok: queries the ngrok local API at http://ngrok:4040/api/tunnels
/// and returns the HTTPS tunnel URL so Paymob can reach localhost.
///
/// Falls back to App:BackendBaseUrl silently if ngrok is not running.
/// </summary>
public interface IPublicUrlResolver
{
    /// <summary>Returns the public base URL to use as the Paymob webhook callback host.</summary>
    Task<string> GetWebhookBaseUrlAsync(CancellationToken ct = default);
}

public sealed class NgrokPublicUrlResolver : IPublicUrlResolver
{
    private readonly HttpClient _http;
    private readonly string _fallbackUrl;

    public NgrokPublicUrlResolver(HttpClient http, IConfiguration config)
    {
        _http = http;
        _fallbackUrl = (config["App:BackendBaseUrl"] ?? "http://localhost:5063").TrimEnd('/');
    }

    public async Task<string> GetWebhookBaseUrlAsync(CancellationToken ct = default)
    {
        try
        {
            // ngrok exposes its management API on port 4040 inside the Docker network.
            var res = await _http.GetAsync("http://ngrok:4040/api/tunnels", ct);
            if (!res.IsSuccessStatusCode) return _fallbackUrl;

            var json = await res.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("tunnels", out var tunnels)) return _fallbackUrl;

            // Prefer the HTTPS tunnel; ngrok always creates one.
            foreach (var tunnel in tunnels.EnumerateArray())
            {
                if (tunnel.TryGetProperty("proto", out var proto)
                    && proto.GetString() == "https"
                    && tunnel.TryGetProperty("public_url", out var url))
                {
                    return url.GetString()?.TrimEnd('/') ?? _fallbackUrl;
                }
            }
        }
        catch
        {
            // ngrok not running or not reachable — use the configured fallback.
        }

        return _fallbackUrl;
    }
}
