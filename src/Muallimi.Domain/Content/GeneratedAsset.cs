using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Content;

public class GeneratedAsset
{
    public Guid AssetId { get; private set; }
    public Guid LessonId { get; private set; }
    public AssetType AssetType { get; private set; }
    public VisualFormat? VisualFormat { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string? Transcript { get; private set; }
    public string Language { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string ProducedBy { get; private set; } = string.Empty;
    public DateTime ProducedAt { get; private set; }

    /// <summary>JSON object with provider, units, amount.</summary>
    public string? Cost { get; private set; }

    public AssetStatus Status { get; private set; }

    private GeneratedAsset() { }

    /// <summary>
    /// Deterministic asset ID derived from scope, lesson, asset type, and version.
    /// </summary>
    public static Guid ComputeDeterministicId(
        Guid lessonId, AssetType assetType, VisualFormat? visualFormat, int version)
    {
        var input = $"{lessonId}:{assetType}:{visualFormat}:{version}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static GeneratedAsset Create(
        Guid lessonId,
        AssetType assetType,
        VisualFormat? visualFormat,
        string language,
        int version,
        string producedBy)
    {
        if (assetType == AssetType.Visual && visualFormat is null)
            throw new InvalidOperationException("VisualFormat is required when AssetType is Visual.");

        var assetId = ComputeDeterministicId(lessonId, assetType, visualFormat, version);

        return new GeneratedAsset
        {
            AssetId = assetId,
            LessonId = lessonId,
            AssetType = assetType,
            VisualFormat = visualFormat,
            Language = language,
            Version = version,
            ProducedBy = producedBy,
            ProducedAt = DateTime.UtcNow,
            Status = AssetStatus.Queued
        };
    }

    public void MarkProducing()
    {
        if (Status != AssetStatus.Queued)
            throw new InvalidOperationException($"Cannot mark producing from {Status}.");
        Status = AssetStatus.Producing;
    }

    public void SetStorageKey(string storageKey) => StorageKey = storageKey;

    public void SetTranscript(string transcript) => Transcript = transcript;

    public void SetCost(string costJson) => Cost = costJson;

    public void MarkAutoValidating()
    {
        if (Status != AssetStatus.Producing)
            throw new InvalidOperationException($"Cannot mark auto-validating from {Status}.");
        Status = AssetStatus.AutoValidating;
    }

    public void MarkAutoFailed()
    {
        if (Status != AssetStatus.AutoValidating)
            throw new InvalidOperationException($"Cannot mark auto-failed from {Status}.");
        Status = AssetStatus.AutoFailed;
    }

    public void MarkPendingAdminReview()
    {
        if (Status != AssetStatus.AutoValidating)
            throw new InvalidOperationException($"Cannot mark pending admin review from {Status}.");
        Status = AssetStatus.PendingAdminReview;
    }

    public void MarkPendingExpertReview()
    {
        if (Status != AssetStatus.PendingAdminReview)
            throw new InvalidOperationException($"Cannot mark pending expert review from {Status}.");
        Status = AssetStatus.PendingExpertReview;
    }

    public void MarkApproved()
    {
        if (Status != AssetStatus.PendingExpertReview)
            throw new InvalidOperationException($"Cannot approve from {Status}.");
        Status = AssetStatus.Approved;
    }

    public void MarkRejected()
    {
        if (Status != AssetStatus.PendingExpertReview && Status != AssetStatus.PendingAdminReview)
            throw new InvalidOperationException($"Cannot reject from {Status}.");
        Status = AssetStatus.Rejected;
    }

    public void MarkEditRequested()
    {
        if (Status != AssetStatus.PendingExpertReview && Status != AssetStatus.PendingAdminReview)
            throw new InvalidOperationException($"Cannot request edit from {Status}.");
        Status = AssetStatus.EditRequested;
    }

    public void MarkInvalidated()
    {
        if (Status != AssetStatus.Approved)
            throw new InvalidOperationException($"Cannot invalidate from {Status}.");
        Status = AssetStatus.Invalidated;
    }

    public void MarkSuperseded()
    {
        if (Status != AssetStatus.Approved)
            throw new InvalidOperationException($"Cannot supersede from {Status}.");
        Status = AssetStatus.Superseded;
    }

    /// <summary>Reset to queued for regeneration after rejection or edit request.</summary>
    public void ResetForRegeneration(int newVersion)
    {
        if (Status != AssetStatus.AutoFailed && Status != AssetStatus.Rejected && Status != AssetStatus.EditRequested)
            throw new InvalidOperationException($"Cannot regenerate from {Status}.");
        Version = newVersion;
        AssetId = ComputeDeterministicId(LessonId, AssetType, VisualFormat, Version);
        Status = AssetStatus.Queued;
        StorageKey = string.Empty;
        Transcript = null;
        Cost = null;
        ProducedAt = DateTime.UtcNow;
    }
}
