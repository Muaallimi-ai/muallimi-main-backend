using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Publication;

/// <summary>
/// An approved GeneratedAsset retrievable by the learning engine.
/// PublishedId equals the approved asset_id (deterministic).
/// Only 'active' published assets are returned by runtime retrieval.
/// Approved assets are immutable; updates require a new version with a new asset_id.
/// </summary>
public class PublishedAsset
{
    public Guid PublishedId { get; private set; }
    public Guid LessonId { get; private set; }
    public AssetType AssetType { get; private set; }
    public VisualFormat? VisualFormat { get; private set; }
    public string RuntimeUrl { get; private set; } = string.Empty;
    public string ApprovedByAdmin { get; private set; } = string.Empty;
    public string ApprovedByExpert { get; private set; } = string.Empty;
    public DateTime ApprovedAt { get; private set; }
    public int Version { get; private set; }
    public PublishedAssetStatus Status { get; private set; }

    private PublishedAsset() { }

    /// <summary>
    /// Create a published asset from an approved GeneratedAsset.
    /// The published ID equals the asset ID (deterministic).
    /// </summary>
    public static PublishedAsset Create(
        Guid assetId,
        Guid lessonId,
        AssetType assetType,
        VisualFormat? visualFormat,
        string runtimeUrl,
        string approvedByAdmin,
        string approvedByExpert,
        int version)
    {
        return new PublishedAsset
        {
            PublishedId = assetId,
            LessonId = lessonId,
            AssetType = assetType,
            VisualFormat = visualFormat,
            RuntimeUrl = runtimeUrl,
            ApprovedByAdmin = approvedByAdmin,
            ApprovedByExpert = approvedByExpert,
            ApprovedAt = DateTime.UtcNow,
            Version = version,
            Status = PublishedAssetStatus.Active
        };
    }

    public void Invalidate()
    {
        if (Status != PublishedAssetStatus.Active)
            throw new InvalidOperationException($"Cannot invalidate from {Status}.");
        Status = PublishedAssetStatus.Invalidated;
    }

    public void MarkReplaced()
    {
        if (Status != PublishedAssetStatus.Active)
            throw new InvalidOperationException($"Cannot replace from {Status}.");
        Status = PublishedAssetStatus.Replaced;
    }

    public bool IsActive => Status == PublishedAssetStatus.Active;
}
