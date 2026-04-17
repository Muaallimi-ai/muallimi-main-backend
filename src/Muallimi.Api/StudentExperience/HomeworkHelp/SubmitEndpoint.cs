using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.HomeworkHelp;

/// <summary>
/// T107 (US7) — Homework Help endpoints.
///
///   POST /api/student/homework-help/submit  — multipart submit; routes
///                                             text / voice / image to the
///                                             Phase 2 tutor runtime via
///                                             <see cref="IHomeworkHelpService"/>.
///   GET  /api/student/homework-help/{id}    — return the persisted
///                                             submission + cached response
///                                             so the student can resume.
///
/// The submit handler emits a <c>homework_help_used</c> session event in
/// the same unit of work as the persisted response so the Phase 4 fan-out
/// is atomic with the row mutation. Refusals emit the same event with the
/// refusal reason on the payload so the engagement pipeline can surface
/// non-solving moments distinctly from answered ones.
/// </summary>
public static class HomeworkHelpEndpoints
{
    public const string SubmitRoute = "/api/student/homework-help/submit";
    public const string GetRoute = "/api/student/homework-help/{id:guid}";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapHomeworkHelp(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(SubmitRoute, HandleSubmitAsync)
            .WithName("StudentHomeworkHelpSubmit")
            .WithTags("StudentExperience")
            .DisableAntiforgery();

        routes.MapGet(GetRoute, HandleGetAsync)
            .WithName("StudentHomeworkHelpGet")
            .WithTags("StudentExperience");

        return routes;
    }

    public static async Task<IResult> HandleSubmitAsync(
        HttpContext http,
        IHomeworkHelpService service,
        IHomeworkHelpSubmissionRepository repository,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data is required." });

        var form = await http.Request.ReadFormAsync(ct);
        var parsed = ParseSubmitForm(form);
        if (parsed.Error is not null) return Results.BadRequest(new { error = parsed.Error });

        var session = await sessions.FindAsync(parsed.Request.SessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId) return Results.NotFound();

        Stream? imageStream = null;
        if (parsed.ImageFile is not null)
            imageStream = parsed.ImageFile.OpenReadStream();

        HomeworkHelpResult result;
        try
        {
            result = await service.SubmitAsync(
                session: session,
                profile: profile,
                request: parsed.Request,
                imageStream: imageStream,
                imageContentType: parsed.ImageFile?.ContentType,
                ct: ct);
        }
        finally
        {
            if (imageStream is not null) await imageStream.DisposeAsync();
        }

        if (result.Outcome == HomeworkHelpOutcome.InvalidRequest)
            return Results.BadRequest(new { error = result.Error });

        var submission = result.Submission!;
        var response = result.Response! with { SubmissionId = submission.Id };

        await outbox.EnqueueAsync(
            kind: SessionEventKind.homework_help_used,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                submission_id = submission.Id,
                input_modality = submission.InputModality,
                final_outcome = response.FinalOutcome,
                refusal_reason = response.RefusalReason,
                ocr_adapter_binding_id = submission.OcrAdapterBindingId,
                ai_request_record_id = response.AiRequestRecordId,
                confidence_signal = response.ConfidenceSignal,
            },
            curriculumScope: new CurriculumScope(
                CurriculumType: profile.CurriculumType,
                Grade: profile.Grade,
                SubjectId: parsed.Request.SubjectId == Guid.Empty ? session.ActiveSubjectId : parsed.Request.SubjectId,
                ChapterId: session.ActiveChapterId,
                TopicId: parsed.Request.TopicId ?? session.ActiveTopicId,
                LessonId: session.ActiveLessonId),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);
        await db.SaveChangesAsync(ct);

        http.Response.Headers["X-Correlation-Id"] = session.CorrelationId.ToString();
        return Results.Json(response, SerializerOptions);
    }

    public static async Task<IResult> HandleGetAsync(
        HttpContext http,
        Guid id,
        IHomeworkHelpSubmissionRepository repository,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var submission = await repository.FindAsync(id, ct);
        if (submission is null || submission.TenantId != tenantId) return Results.NotFound();

        var response = repository.ReadResponse(submission);
        if (response is not null && response.SubmissionId == Guid.Empty)
            response = response with { SubmissionId = submission.Id };

        var payload = new HomeworkHelpGetResponse(
            SubmissionId: submission.Id,
            SessionId: submission.StudentSessionId,
            InputModality: submission.InputModality,
            FinalOutcome: submission.FinalOutcome ?? "pending",
            ExtractedProblemText: submission.ExtractedProblemText,
            TextPayload: submission.InputModality == HomeworkHelpModalities.Image ? null : submission.TextPayload,
            VoiceCaptureId: submission.VoiceCaptureId,
            ImageBlobReference: submission.ImageBlobReference,
            ImagePreprocessMetadata: repository.ReadImageMetadata(submission),
            OcrAdapterBindingId: submission.OcrAdapterBindingId,
            AiRequestRecordId: submission.AiRequestRecordId,
            RetentionUntil: submission.RetentionUntil,
            CreatedAt: submission.CreatedAt,
            Response: response);

        return Results.Json(payload, SerializerOptions);
    }

    public static SubmitFormParseResult ParseSubmitForm(IFormCollection form)
    {
        if (!Guid.TryParse(form["session_id"], out var sessionId) || sessionId == Guid.Empty)
            return SubmitFormParseResult.WithError("session_id is required.");

        Guid.TryParse(form["correlation_id"], out var correlationId);

        var modalityRaw = form["input_modality"].ToString();
        if (string.IsNullOrWhiteSpace(modalityRaw))
            return SubmitFormParseResult.WithError("input_modality is required.");
        if (!HomeworkHelpModalities.IsAccepted(modalityRaw))
            return SubmitFormParseResult.WithError("input_modality is invalid.");

        var textPayload = form.TryGetValue("text_payload", out var tp) ? tp.ToString() : null;
        Guid? voiceCaptureId = Guid.TryParse(form["voice_capture_id"], out var vc) ? vc : null;
        Guid.TryParse(form["subject_id"], out var subjectId);
        Guid? topicId = Guid.TryParse(form["topic_id"], out var topic) ? topic : null;
        var tutorLanguage = form["tutor_language"].ToString();
        if (string.IsNullOrWhiteSpace(tutorLanguage)) tutorLanguage = "ar";

        HomeworkImagePreprocessMetadata? metadata = null;
        var metadataJson = form.TryGetValue("image_preprocess_metadata", out var mp) ? mp.ToString() : null;
        if (!string.IsNullOrWhiteSpace(metadataJson))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<HomeworkImagePreprocessMetadata>(
                    metadataJson, SerializerOptions);
            }
            catch (JsonException)
            {
                return SubmitFormParseResult.WithError("image_preprocess_metadata is not valid JSON.");
            }
        }

        var imageFile = form.Files.GetFile("image_blob");

        var request = new HomeworkHelpSubmitRequest(
            SessionId: sessionId,
            CorrelationId: correlationId,
            InputModality: modalityRaw,
            TextPayload: textPayload,
            VoiceCaptureId: voiceCaptureId,
            SubjectId: subjectId,
            TopicId: topicId,
            TutorLanguage: tutorLanguage,
            ImagePreprocessMetadata: metadata);

        return new SubmitFormParseResult(request, imageFile, null);
    }
}

public sealed record SubmitFormParseResult(
    HomeworkHelpSubmitRequest Request,
    IFormFile? ImageFile,
    string? Error)
{
    public static SubmitFormParseResult WithError(string error) =>
        new(
            Request: new HomeworkHelpSubmitRequest(
                SessionId: Guid.Empty,
                CorrelationId: Guid.Empty,
                InputModality: HomeworkHelpModalities.Text,
                TextPayload: null,
                VoiceCaptureId: null,
                SubjectId: Guid.Empty,
                TopicId: null,
                TutorLanguage: "ar",
                ImagePreprocessMetadata: null),
            ImageFile: null,
            Error: error);
}
