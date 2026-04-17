using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T073 (US4) — POST /student/tutor/voice.
///
/// Accepts a captured opus/webm blob, runs STT through a Phase 2 adapter,
/// forwards the transcript to the Phase 2 tutor runtime via
/// <see cref="ITutorRuntimeClient.AskAsync"/>, synthesises the answer audio
/// through the Phase 2 AI tutor voice profile binding, and returns the
/// playback reference plus a text transcript.
///
/// The facade NEVER generates answer text locally — every answered turn
/// re-emits what the Phase 2 runtime returned, preserving the guardrail
/// chain. Audio TTS is allowed because it converts already-grounded text to
/// speech; the resolved <c>voice_profile_id</c> is asserted against the
/// Phase 2 AI-tutor pinned set (<see cref="Phase2AiTutorVoiceProfiles"/>)
/// and rejected if it collides with any Phase 1 teacher voice id (FR-019).
/// </summary>
public static class TutorVoiceEndpoint
{
    public const string Route = "/api/student/tutor/voice";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapTutorVoiceChat(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .WithName("StudentTutorVoiceChat")
            .WithTags("StudentExperience")
            .DisableAntiforgery();
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        IStudentSessionRepository sessions,
        ITutorRuntimeClient tutorRuntime,
        ITutorChatMessageRepository chatMessages,
        IVoiceCaptureRepository voiceCaptures,
        ISttClient stt,
        IAnswerAudioSynthesizer synthesizer,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        if (!http.Request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data is required." });

        var form = await http.Request.ReadFormAsync(ct);
        var parsed = ParseMultipartBody(form);
        if (parsed.Error is not null) return Results.BadRequest(new { error = parsed.Error });

        var session = await sessions.FindAsync(parsed.SessionId, ct);
        if (session is null || session.TenantId != tenantId)
            return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId)
            return Results.NotFound();

        var language = TutorTextEndpoint.NormaliseLanguage(parsed.TutorLanguage, session.TutorLanguage);
        var turnNumber = parsed.TurnNumber > 0
            ? parsed.TurnNumber
            : await chatMessages.NextTurnNumberAsync(session.Id, ct);

        // 1. Persist the captured blob + voice-capture row (T074).
        await using var audioStream = parsed.AudioFile!.OpenReadStream();
        var capture = await voiceCaptures.RecordCaptureAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            codec: parsed.Codec,
            durationMs: parsed.DurationMs,
            audioStream: audioStream,
            ct: ct);

        // 2. STT: transcribe captured audio.
        var blob = await voiceCaptures.GetCapturedBlobAsync(capture.BlobReference, ct)
                   ?? throw new InvalidOperationException("Voice capture blob disappeared after persistence.");
        SttResult sttResult;
        await using (var sttStream = blob.Content)
        {
            sttResult = await stt.TranscribeAsync(new SttRequest(
                AudioStream: sttStream,
                ContentType: parsed.Codec,
                TutorLanguage: language,
                CorrelationId: session.CorrelationId), ct);
        }

        var transcriptText = (sttResult.Transcript ?? string.Empty).Trim();
        if (transcriptText.Length == 0)
            return Results.BadRequest(new { error = "transcription produced no text." });

        // 3. Persist the student voice turn (mark capture transcribed).
        var studentTurn = await chatMessages.AppendStudentTurnAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            turnNumber: turnNumber,
            language: language,
            questionText: transcriptText,
            ct: ct);
        studentTurn.Modality = "voice";
        studentTurn.VoiceCaptureReference = capture.BlobReference;
        await voiceCaptures.MarkTranscribedAsync(
            capture: capture,
            tutorChatMessageId: studentTurn.Id,
            transcriptText: transcriptText,
            sttAdapterBindingId: sttResult.AdapterBindingId,
            ct: ct);

        await outbox.EnqueueAsync(
            kind: SessionEventKind.question_asked,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                turn_number = turnNumber,
                modality = "voice",
                tutor_language = language,
                voice_capture_reference = capture.BlobReference,
            },
            curriculumScope: ScopeFor(session, profile),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);

        await db.SaveChangesAsync(ct);

        // 4. Forward the transcript to the Phase 2 tutor runtime (passthrough).
        var upstream = await CallTutorRuntimeWithVoiceAsync(
            tutorRuntime, session, profile, language, transcriptText, sttResult.Confidence, turnNumber, ct);
        var streamResult = TutorTextEndpoint.MapUpstream(upstream, language);

        // 5. Synthesise playback audio for answered + fallback only.
        var playback = TutorTextEndpoint.IsRefusal(streamResult.Final)
            ? AnswerAudioSynthesisResult.None
            : await synthesizer.SynthesiseAsync(
                new AnswerAudioRequest(
                    AnswerText: streamResult.AnswerText ?? string.Empty,
                    TutorLanguage: language,
                    StudentSessionId: session.Id,
                    CorrelationId: session.CorrelationId),
                ct);

        // 6. Persist tutor voice turn linked to Phase 2 AiRequestRecord (T061).
        var tutorTurn = await chatMessages.AppendTutorTurnAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            turnNumber: turnNumber,
            language: language,
            answerText: streamResult.AnswerText,
            aiRequestRecordId: TutorTextEndpoint.TryParseGuid(streamResult.Final.AiRequestRecordId),
            guardrailFinalStage: streamResult.Final.GuardrailFinalStage,
            finalOutcome: streamResult.Final.FinalOutcome,
            confidenceSignal: streamResult.Confidence.ConfidenceSignal,
            evidenceRefs: streamResult.EvidenceRefs,
            ct: ct);
        tutorTurn.Modality = "voice";
        tutorTurn.VoicePlaybackReference = playback.PlaybackReference;

        await outbox.EnqueueAsync(
            kind: TutorTextEndpoint.IsRefusal(streamResult.Final)
                ? SessionEventKind.refusal
                : SessionEventKind.answer_received,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                turn_number = turnNumber,
                modality = "voice",
                final_outcome = streamResult.Final.FinalOutcome,
                confidence_signal = streamResult.Confidence.ConfidenceSignal,
                ai_request_record_id = streamResult.Final.AiRequestRecordId,
                guardrail_final_stage = streamResult.Final.GuardrailFinalStage,
                voice_profile_id = playback.VoiceProfileId,
                voice_playback_reference = playback.PlaybackReference,
            },
            curriculumScope: ScopeFor(session, profile),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);

        await db.SaveChangesAsync(ct);

        // 7. Hand the playback reference back to the client.
        var response = new TutorVoiceResponse(
            TurnNumber: turnNumber,
            TranscriptText: transcriptText,
            AnswerText: streamResult.AnswerText ?? string.Empty,
            VoicePlaybackReference: playback.PlaybackReference ?? string.Empty,
            VoiceProfileId: playback.VoiceProfileId ?? string.Empty,
            VoiceProfileSource: Phase2AiTutorVoiceProfiles.Source,
            FinalOutcome: streamResult.Final.FinalOutcome,
            ConfidenceSignal: streamResult.Confidence.ConfidenceSignal,
            EvidenceRefs: streamResult.EvidenceRefs,
            AiRequestRecordId: streamResult.Final.AiRequestRecordId,
            RefusalTextAr: streamResult.Final.RefusalTextAr,
            RefusalTextEn: streamResult.Final.RefusalTextEn);

        http.Response.Headers["X-Correlation-Id"] = session.CorrelationId.ToString();
        return Results.Json(response, SerializerOptions);
    }

    public static MultipartParseResult ParseMultipartBody(IFormCollection form)
    {
        var file = form.Files.GetFile(TutorVoiceRequestParts.DefaultAudioBlobFieldName);
        if (file is null || file.Length == 0)
            return MultipartParseResult.WithError("audio_blob is required.");

        if (!Guid.TryParse(form["session_id"], out var sessionId))
            return MultipartParseResult.WithError("session_id is required.");

        Guid.TryParse(form["correlation_id"], out var correlationId);

        var codecRaw = form["codec"].ToString();
        var codec = string.IsNullOrWhiteSpace(codecRaw)
            ? (file.ContentType ?? "audio/webm")
            : codecRaw;
        if (!TutorVoiceMediaTypes.IsAcceptedCaptureCodec(codec))
            codec = TutorVoiceMediaTypes.AcceptedCaptureCodecs.First();

        int.TryParse(form["turn_number"], out var turnNumber);
        int.TryParse(form["duration_ms"], out var durationMs);

        var tutorLanguage = form["tutor_language"].ToString();
        if (string.IsNullOrWhiteSpace(tutorLanguage)) tutorLanguage = "ar";

        return new MultipartParseResult(
            SessionId: sessionId,
            CorrelationId: correlationId,
            TurnNumber: turnNumber,
            DurationMs: durationMs,
            Codec: codec,
            TutorLanguage: tutorLanguage,
            AudioFile: file,
            Error: null);
    }

    public static async Task<UpstreamTutorResponse> CallTutorRuntimeWithVoiceAsync(
        ITutorRuntimeClient runtime,
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        string language,
        string transcriptText,
        double? transcriptionConfidence,
        int turnNumber,
        CancellationToken ct)
    {
        var body = new
        {
            request_id = Guid.NewGuid().ToString("N"),
            correlation_id = session.CorrelationId.ToString(),
            session_id = session.Id.ToString(),
            tenant_id = session.TenantId.ToString(),
            student_id_hash = session.StudentProfileId.ToString("N"),
            session_scope = new
            {
                curriculum_type = profile.CurriculumType,
                grade = profile.Grade,
                subject = session.ActiveSubjectId?.ToString() ?? string.Empty,
                active_lesson_id = session.ActiveLessonId?.ToString() ?? string.Empty,
                active_topic_id = session.ActiveTopicId?.ToString(),
                tutor_language = language,
                grade_band = "achievers",
                session_mode = "tutor_voice",
            },
            question = new
            {
                source = "voice_transcribed",
                text = transcriptText,
                transcription_confidence = transcriptionConfidence,
            },
            submitted_at = DateTime.UtcNow,
            turn_number = turnNumber,
        };

        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        using var response = await runtime.AskAsync(stream, "application/json", ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return new UpstreamTutorResponse(response.IsSuccessStatusCode, raw);
    }

    private static CurriculumScope ScopeFor(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile) => new(
            CurriculumType: profile.CurriculumType,
            Grade: profile.Grade,
            SubjectId: session.ActiveSubjectId,
            ChapterId: session.ActiveChapterId,
            TopicId: session.ActiveTopicId,
            LessonId: session.ActiveLessonId);
}

public sealed record MultipartParseResult(
    Guid SessionId,
    Guid CorrelationId,
    int TurnNumber,
    int DurationMs,
    string Codec,
    string TutorLanguage,
    IFormFile? AudioFile,
    string? Error)
{
    public static MultipartParseResult WithError(string error) => new(
        SessionId: Guid.Empty,
        CorrelationId: Guid.Empty,
        TurnNumber: 0,
        DurationMs: 0,
        Codec: string.Empty,
        TutorLanguage: "ar",
        AudioFile: null,
        Error: error);
}

// ── STT abstraction ─────────────────────────────────────────────────────

public interface ISttClient
{
    Task<SttResult> TranscribeAsync(SttRequest request, CancellationToken ct = default);
}

public sealed record SttRequest(
    Stream AudioStream,
    string ContentType,
    string TutorLanguage,
    Guid CorrelationId);

public sealed record SttResult(
    string Transcript,
    string DetectedLanguage,
    double? Confidence,
    Guid? AdapterBindingId,
    string ProviderIdentifier);

/// <summary>
/// Local-mode STT stub. Production swaps this for the Phase 2 ai-service
/// adapter binding without touching the endpoint surface.
/// </summary>
public sealed class LocalEchoSttClient : ISttClient
{
    public Task<SttResult> TranscribeAsync(SttRequest request, CancellationToken ct = default)
    {
        var transcript = request.TutorLanguage == "ar"
            ? "ما هو شرح الدرس بإيجاز؟"
            : "Please summarise this lesson briefly.";
        return Task.FromResult(new SttResult(
            Transcript: transcript,
            DetectedLanguage: request.TutorLanguage,
            Confidence: 0.85,
            AdapterBindingId: null,
            ProviderIdentifier: "local-echo-stt"));
    }
}

// ── Answer audio synthesis (TTS via Phase 2 AI tutor voice binding) ─────

public interface IAnswerAudioSynthesizer
{
    Task<AnswerAudioSynthesisResult> SynthesiseAsync(AnswerAudioRequest request, CancellationToken ct = default);
}

public sealed record AnswerAudioRequest(
    string AnswerText,
    string TutorLanguage,
    Guid StudentSessionId,
    Guid CorrelationId);

public sealed record AnswerAudioSynthesisResult(
    string? PlaybackReference,
    string? VoiceProfileId,
    string ContentType,
    int DurationMs)
{
    public static readonly AnswerAudioSynthesisResult None = new(null, null, TutorVoiceMediaTypes.PlaybackContentType, 0);
}

/// <summary>
/// Local-mode synthesiser. Stores a tiny WebM placeholder in the in-memory
/// blob store and pins the Phase 2 AI tutor voice profile id. Production
/// swaps the implementation for the ai-service TTS adapter.
///
/// FR-019 invariant: the resolved profile id MUST be in
/// <see cref="Phase2AiTutorVoiceProfiles.All"/> AND MUST NOT appear in
/// <c>Phase1TeacherVoiceProfiles.All</c>. This synthesiser fails closed if
/// the invariant is violated; the cross-phase test (T080) asserts the same
/// on every commit.
/// </summary>
public sealed class LocalAnswerAudioSynthesizer : IAnswerAudioSynthesizer
{
    private readonly IVoiceBlobStore _blobs;

    public LocalAnswerAudioSynthesizer(IVoiceBlobStore blobs)
    {
        _blobs = blobs;
    }

    public Task<AnswerAudioSynthesisResult> SynthesiseAsync(
        AnswerAudioRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.AnswerText))
            return Task.FromResult(AnswerAudioSynthesisResult.None);

        var voiceProfileId = Phase2AiTutorVoiceProfiles.DefaultProfileId;
        if (!Phase2AiTutorVoiceProfiles.IsAiTutorProfileId(voiceProfileId))
            throw new InvalidOperationException(
                $"Resolved voice profile '{voiceProfileId}' is not in the Phase 2 AI tutor pinned set (FR-019).");

        // FR-019: Phase 1 teacher voice ids must NEVER be reused for live tutor
        // playback. Cross-checked in test T080.
        if (LessonRetrieval.Phase1TeacherVoiceProfiles.All.Contains(voiceProfileId))
            throw new InvalidOperationException(
                $"AI tutor voice profile '{voiceProfileId}' collides with a Phase 1 teacher voice profile (FR-019).");

        var payload = System.Text.Encoding.UTF8.GetBytes($"local-tts-stub::{request.AnswerText.Length}");
        var reference = _blobs.Persist($"playback/{request.StudentSessionId:N}", payload, TutorVoiceMediaTypes.PlaybackContentType);

        return Task.FromResult(new AnswerAudioSynthesisResult(
            PlaybackReference: reference,
            VoiceProfileId: voiceProfileId,
            ContentType: TutorVoiceMediaTypes.PlaybackContentType,
            DurationMs: Math.Max(500, request.AnswerText.Length * 60)));
    }
}

public static class TutorVoiceEndpointServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3TutorVoiceEndpoint(this IServiceCollection services)
    {
        services.AddSingleton<ISttClient, LocalEchoSttClient>();
        services.AddSingleton<IAnswerAudioSynthesizer, LocalAnswerAudioSynthesizer>();
        return services;
    }
}
