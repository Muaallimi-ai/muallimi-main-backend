using Muallimi.Domain.Shared;

namespace Muallimi.Api.Publication;

/// <summary>
/// T095 - Maps deterministic asset IDs to logical CDN URLs.
/// In development: local static file URLs.
/// In production: Phase 0 CDN URL schema.
/// </summary>
public class LogicalCdnUrlProvider
{
    private readonly bool _isDevelopment;
    private readonly string _cdnBaseUrl;
    private readonly string _localBaseUrl;

    public LogicalCdnUrlProvider(IWebHostEnvironment env, IConfiguration config)
    {
        _isDevelopment = env.IsDevelopment();
        _cdnBaseUrl = config["Cdn:BaseUrl"] ?? "https://cdn.muallimi.com";
        _localBaseUrl = config["Cdn:LocalBaseUrl"] ?? "/static/content";
    }

    /// <summary>
    /// Generates a logical CDN URL for a published asset.
    /// Format: /{base}/{lessonId}/{assetType}/{assetId}.{ext}
    /// </summary>
    public string GenerateUrl(Guid lessonId, AssetType assetType, Guid assetId, VisualFormat? visualFormat = null)
    {
        var ext = ResolveExtension(assetType, visualFormat);
        var typePath = assetType.ToString().ToLowerInvariant();

        if (_isDevelopment)
        {
            return $"{_localBaseUrl}/{lessonId}/{typePath}/{assetId}{ext}";
        }

        return $"{_cdnBaseUrl}/content/{lessonId}/{typePath}/{assetId}{ext}";
    }

    /// <summary>
    /// Generates an audio URL for a specific chunk.
    /// </summary>
    public string GenerateAudioUrl(Guid chunkId)
    {
        if (_isDevelopment)
        {
            return $"{_localBaseUrl}/audio/{chunkId}.mp3";
        }

        return $"{_cdnBaseUrl}/content/audio/{chunkId}.mp3";
    }

    /// <summary>
    /// Generates a transcript URL for visual/audio assets.
    /// </summary>
    public string GenerateTranscriptUrl(Guid lessonId, Guid assetId)
    {
        if (_isDevelopment)
        {
            return $"{_localBaseUrl}/{lessonId}/transcript/{assetId}.vtt";
        }

        return $"{_cdnBaseUrl}/content/{lessonId}/transcript/{assetId}.vtt";
    }

    private static string ResolveExtension(AssetType assetType, VisualFormat? visualFormat)
    {
        return assetType switch
        {
            AssetType.Audio => ".mp3",
            AssetType.TextSummary => ".json",
            AssetType.QuizItem => ".json",
            AssetType.QaCacheEntry => ".json",
            AssetType.Visual => visualFormat switch
            {
                VisualFormat.Mp4Animation => ".mp4",
                VisualFormat.InteractiveHtml => ".html",
                VisualFormat.Whiteboard => ".mp4",
                VisualFormat.Diagram => ".svg",
                _ => ".bin"
            },
            _ => ".bin"
        };
    }
}
