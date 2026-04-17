using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T074 (US4) — Persistence + blob pointer wiring for voice captures.
///
/// Each captured audio blob produces:
///   - a row in <see cref="VoiceCapture"/> tracking upload + STT state and a
///     <c>retention_until</c> watermark (FR-028, default 30 days) so the
///     Phase 2 retention sweeper can purge expired captures.
///   - a blob entry in the in-memory store (local-mode default), keyed by the
///     same reference recorded on the row. Production swaps the store for the
///     Phase 0 MinIO/S3 adapter without changing this surface.
///
/// Reads are added to the change tracker only; the caller commits the
/// surrounding unit of work alongside the outbox + chat-message rows.
/// </summary>
public interface IVoiceCaptureRepository
{
    Task<VoiceCapture> RecordCaptureAsync(
        Guid tenantId,
        Guid studentSessionId,
        string codec,
        int durationMs,
        Stream audioStream,
        CancellationToken ct = default);

    Task MarkTranscribedAsync(
        VoiceCapture capture,
        Guid? tutorChatMessageId,
        string transcriptText,
        Guid? sttAdapterBindingId,
        CancellationToken ct = default);

    Task<VoiceBlob?> GetCapturedBlobAsync(string blobReference, CancellationToken ct = default);
}

public sealed record VoiceBlob(
    string BlobReference,
    string ContentType,
    Stream Content);

public interface IVoiceBlobStore
{
    string Persist(string scopeKey, byte[] payload, string contentType);
    VoiceBlob? Read(string blobReference);
}

/// <summary>
/// Local in-memory blob store. Phase 0 / local-mode default; production swaps
/// in the MinIO adapter via DI replacement.
/// </summary>
public sealed class InMemoryVoiceBlobStore : IVoiceBlobStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] Payload, string ContentType)> _store = new();

    public string Persist(string scopeKey, byte[] payload, string contentType)
    {
        var reference = $"local-blob://voice/{scopeKey}/{Guid.NewGuid():N}";
        _store[reference] = (payload, contentType);
        return reference;
    }

    public VoiceBlob? Read(string blobReference)
    {
        if (!_store.TryGetValue(blobReference, out var entry)) return null;
        return new VoiceBlob(blobReference, entry.ContentType, new MemoryStream(entry.Payload, writable: false));
    }
}

public sealed class VoiceCaptureRepository : IVoiceCaptureRepository
{
    /// <summary>
    /// FR-028 — voice captures default to a 30-day retention window. The
    /// Phase 2 retention sweeper deletes blob + row past <c>retention_until</c>.
    /// </summary>
    public static readonly TimeSpan DefaultRetentionWindow = TimeSpan.FromDays(30);

    private readonly MuallimiDbContext _db;
    private readonly IVoiceBlobStore _blobs;

    public VoiceCaptureRepository(MuallimiDbContext db, IVoiceBlobStore blobs)
    {
        _db = db;
        _blobs = blobs;
    }

    public async Task<VoiceCapture> RecordCaptureAsync(
        Guid tenantId,
        Guid studentSessionId,
        string codec,
        int durationMs,
        Stream audioStream,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var blobRef = _blobs.Persist(studentSessionId.ToString("N"), bytes, codec);

        var row = new VoiceCapture
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            BlobReference = blobRef,
            Codec = codec,
            DurationMs = durationMs,
            UploadState = "uploaded",
            SttState = "pending",
            RetentionUntil = DateTime.UtcNow.Add(DefaultRetentionWindow),
        };
        _db.VoiceCaptures.Add(row);
        return row;
    }

    public Task MarkTranscribedAsync(
        VoiceCapture capture,
        Guid? tutorChatMessageId,
        string transcriptText,
        Guid? sttAdapterBindingId,
        CancellationToken ct = default)
    {
        capture.SttState = "transcribed";
        capture.TranscriptText = transcriptText;
        capture.TutorChatMessageId = tutorChatMessageId;
        capture.SttAdapterBindingId = sttAdapterBindingId;
        return Task.CompletedTask;
    }

    public Task<VoiceBlob?> GetCapturedBlobAsync(string blobReference, CancellationToken ct = default)
    {
        return Task.FromResult(_blobs.Read(blobReference));
    }
}

public static class VoiceCaptureRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3VoiceCaptureRepository(this IServiceCollection services)
    {
        services.AddSingleton<IVoiceBlobStore, InMemoryVoiceBlobStore>();
        services.AddScoped<IVoiceCaptureRepository, VoiceCaptureRepository>();
        return services;
    }
}
