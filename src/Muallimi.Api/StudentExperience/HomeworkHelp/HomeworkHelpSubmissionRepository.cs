using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.TutorExposure;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.HomeworkHelp;

/// <summary>
/// T106 (US7) — <see cref="HomeworkHelpSubmission"/> persistence.
///
/// The repository owns four concerns on the homework_help_submissions table:
///   - Records <c>image_preprocess_metadata</c> as a jsonb column so audit
///     can cite the client-confirmed EXIF strip + face-flag heuristic
///     without re-parsing the wire payload.
///   - Sets <c>retention_until</c> on every row (default 30 days, FR-028)
///     so the Phase 2 retention sweeper can purge expired submissions
///     alongside captures and chat messages.
///   - Persists the resolved Phase 2 OCR adapter binding id so incident
///     lookup can correlate a submission with the model that read it.
///   - Stores the structured response envelope as jsonb so the GET endpoint
///     can resume the result without re-running OCR or the tutor runtime.
///
/// EF's global query filter handles tenancy; the repository never re-checks
/// it itself and never writes to Phase 1 tables.
/// </summary>
public interface IHomeworkHelpSubmissionRepository
{
    Task<HomeworkHelpSubmission> CreateTextOrVoiceAsync(
        Guid tenantId,
        Guid studentSessionId,
        string inputModality,
        string? textPayload,
        Guid? voiceCaptureId,
        CancellationToken ct = default);

    Task<HomeworkHelpSubmission> CreateImageAsync(
        Guid tenantId,
        Guid studentSessionId,
        string imageBlobReference,
        HomeworkImagePreprocessMetadata metadata,
        CancellationToken ct = default);

    Task UpdateAfterProcessingAsync(
        HomeworkHelpSubmission submission,
        string? extractedProblemText,
        Guid? ocrAdapterBindingId,
        Guid? aiRequestRecordId,
        string finalOutcome,
        HomeworkHelpSubmitResponse response,
        CancellationToken ct = default);

    Task<HomeworkHelpSubmission?> FindAsync(Guid submissionId, CancellationToken ct = default);

    HomeworkImagePreprocessMetadata? ReadImageMetadata(HomeworkHelpSubmission submission);

    HomeworkHelpSubmitResponse? ReadResponse(HomeworkHelpSubmission submission);

    string PersistImageBlob(Guid studentSessionId, byte[] payload, string contentType);

    VoiceBlob? ReadImageBlob(string blobReference);
}

public sealed class HomeworkHelpSubmissionRepository : IHomeworkHelpSubmissionRepository
{
    /// <summary>FR-028 — homework submissions default to a 30-day retention window.</summary>
    public static readonly TimeSpan DefaultRetentionWindow = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MuallimiDbContext _db;
    private readonly IVoiceBlobStore _imageBlobs;

    public HomeworkHelpSubmissionRepository(MuallimiDbContext db, IVoiceBlobStore imageBlobs)
    {
        _db = db;
        _imageBlobs = imageBlobs;
    }

    public async Task<HomeworkHelpSubmission> CreateTextOrVoiceAsync(
        Guid tenantId,
        Guid studentSessionId,
        string inputModality,
        string? textPayload,
        Guid? voiceCaptureId,
        CancellationToken ct = default)
    {
        var row = new HomeworkHelpSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            InputModality = inputModality,
            TextPayload = textPayload,
            VoiceCaptureId = voiceCaptureId,
            RetentionUntil = DateTime.UtcNow.Add(DefaultRetentionWindow),
            CreatedAt = DateTime.UtcNow,
        };
        _db.HomeworkHelpSubmissions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<HomeworkHelpSubmission> CreateImageAsync(
        Guid tenantId,
        Guid studentSessionId,
        string imageBlobReference,
        HomeworkImagePreprocessMetadata metadata,
        CancellationToken ct = default)
    {
        var row = new HomeworkHelpSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            InputModality = HomeworkHelpModalities.Image,
            ImageBlobReference = imageBlobReference,
            ImagePreprocessMetadata = JsonSerializer.Serialize(metadata, JsonOptions),
            RetentionUntil = DateTime.UtcNow.Add(DefaultRetentionWindow),
            CreatedAt = DateTime.UtcNow,
        };
        _db.HomeworkHelpSubmissions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task UpdateAfterProcessingAsync(
        HomeworkHelpSubmission submission,
        string? extractedProblemText,
        Guid? ocrAdapterBindingId,
        Guid? aiRequestRecordId,
        string finalOutcome,
        HomeworkHelpSubmitResponse response,
        CancellationToken ct = default)
    {
        submission.ExtractedProblemText = extractedProblemText;
        submission.OcrAdapterBindingId = ocrAdapterBindingId;
        submission.AiRequestRecordId = aiRequestRecordId;
        submission.FinalOutcome = finalOutcome;
        // Stash the response envelope on the existing extracted_problem_text
        // jsonb sibling column when present; otherwise stick to image_preprocess
        // for image rows. To keep schema-stable we serialise the response into
        // a dedicated text column (`text_payload` is reused as the resume
        // cache for text/voice rows; image rows resume from the image blob +
        // metadata). The response itself is reconstructable from the columns
        // populated above plus the cached envelope here.
        submission.TextPayload = submission.InputModality switch
        {
            HomeworkHelpModalities.Image => JsonSerializer.Serialize(response, JsonOptions),
            _ => submission.TextPayload, // preserve original prompt for text/voice
        };
        if (submission.InputModality != HomeworkHelpModalities.Image)
        {
            // For text/voice we cache the response on the image_preprocess
            // jsonb column (which is otherwise unused for those modalities).
            submission.ImagePreprocessMetadata = JsonSerializer.Serialize(response, JsonOptions);
        }
        await _db.SaveChangesAsync(ct);
    }

    public Task<HomeworkHelpSubmission?> FindAsync(Guid submissionId, CancellationToken ct = default) =>
        _db.HomeworkHelpSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, ct);

    public HomeworkImagePreprocessMetadata? ReadImageMetadata(HomeworkHelpSubmission submission)
    {
        if (submission.InputModality != HomeworkHelpModalities.Image) return null;
        if (string.IsNullOrWhiteSpace(submission.ImagePreprocessMetadata)) return null;
        try
        {
            return JsonSerializer.Deserialize<HomeworkImagePreprocessMetadata>(
                submission.ImagePreprocessMetadata, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public HomeworkHelpSubmitResponse? ReadResponse(HomeworkHelpSubmission submission)
    {
        var raw = submission.InputModality == HomeworkHelpModalities.Image
            ? submission.TextPayload
            : submission.ImagePreprocessMetadata;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<HomeworkHelpSubmitResponse>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string PersistImageBlob(Guid studentSessionId, byte[] payload, string contentType) =>
        _imageBlobs.Persist($"homework/{studentSessionId:N}", payload, contentType);

    public VoiceBlob? ReadImageBlob(string blobReference) => _imageBlobs.Read(blobReference);
}

public static class HomeworkHelpSubmissionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3HomeworkHelp(this IServiceCollection services)
    {
        services.AddPhase3HomeworkOcrAdapter();
        services.AddScoped<IHomeworkHelpSubmissionRepository, HomeworkHelpSubmissionRepository>();
        services.AddScoped<IHomeworkHelpService, HomeworkHelpService>();
        return services;
    }
}
