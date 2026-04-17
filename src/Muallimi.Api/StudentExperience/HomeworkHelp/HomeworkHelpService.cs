using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.StudentExperience.TutorExposure;
using Muallimi.Domain.StudentExperience;

namespace Muallimi.Api.StudentExperience.HomeworkHelp;

/// <summary>
/// T105 (US7) — HomeworkHelpService.
///
/// Orchestrates the homework-help submission lifecycle:
///   1. Persists the submission row (text, voice, or image) ahead of any
///      generation so a refusal still leaves an audit trail.
///   2. For image submissions, runs the upload through the Phase 2 OCR
///      adapter and treats <see cref="OcrOutcome.Unreadable"/> as a refusal
///      with reason <c>ocr_unreadable</c>.
///   3. Applies <see cref="HomeworkTextRedactor"/> to every problem text
///      before forwarding to the tutor runtime so PII never leaves the
///      facade unredacted (T114, FR-027).
///   4. Forwards the redacted prompt to the Phase 2 tutor runtime with the
///      <c>session_mode = homework_help</c> hint so the upstream guardrail
///      chain refuses any direct-solution request.
///   5. Maps the upstream answer / refusal envelope into the wire shape and
///      stores the response for the GET endpoint to resume from.
///
/// Constitution invariants:
///   - The facade NEVER generates answer text locally; it re-emits whatever
///     the Phase 2 runtime returned, including refusal envelopes.
///   - Image bytes are EXIF-stripped client-side; the server re-checks the
///     <c>exif_stripped</c> flag and refuses if it's missing
///     (refusal_reason = safety_pii).
///   - Every refusal still binds to an <see cref="AiRequestRecord"/> so
///     incident lookup can correlate it with the upstream record id.
/// </summary>
public interface IHomeworkHelpService
{
    Task<HomeworkHelpResult> SubmitAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        HomeworkHelpSubmitRequest request,
        Stream? imageStream,
        string? imageContentType,
        CancellationToken ct = default);
}

public enum HomeworkHelpOutcome
{
    Answered,
    Refused,
    InvalidRequest,
}

public sealed record HomeworkHelpResult(
    HomeworkHelpOutcome Outcome,
    HomeworkHelpSubmission? Submission,
    HomeworkHelpSubmitResponse? Response,
    string? Error);

public sealed class HomeworkHelpService : IHomeworkHelpService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHomeworkHelpSubmissionRepository _submissions;
    private readonly IHomeworkOcrAdapter _ocr;
    private readonly ITutorRuntimeClient _tutorRuntime;

    public HomeworkHelpService(
        IHomeworkHelpSubmissionRepository submissions,
        IHomeworkOcrAdapter ocr,
        ITutorRuntimeClient tutorRuntime)
    {
        _submissions = submissions;
        _ocr = ocr;
        _tutorRuntime = tutorRuntime;
    }

    public async Task<HomeworkHelpResult> SubmitAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        HomeworkHelpSubmitRequest request,
        Stream? imageStream,
        string? imageContentType,
        CancellationToken ct = default)
    {
        if (!HomeworkHelpModalities.IsAccepted(request.InputModality))
            return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null, "input_modality is invalid.");

        if (request.SessionId == Guid.Empty)
            return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null, "session_id is required.");

        var language = TutorTextEndpoint.NormaliseLanguage(request.TutorLanguage, session.TutorLanguage);

        switch (request.InputModality)
        {
            case HomeworkHelpModalities.Text:
                return await HandleTextOrVoiceAsync(session, profile, request, language,
                    HomeworkHelpModalities.Text, ct);
            case HomeworkHelpModalities.Voice:
                return await HandleTextOrVoiceAsync(session, profile, request, language,
                    HomeworkHelpModalities.Voice, ct);
            case HomeworkHelpModalities.Image:
                return await HandleImageAsync(session, profile, request, language,
                    imageStream, imageContentType, ct);
            default:
                return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null,
                    "input_modality is invalid.");
        }
    }

    private async Task<HomeworkHelpResult> HandleTextOrVoiceAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        HomeworkHelpSubmitRequest request,
        string language,
        string modality,
        CancellationToken ct)
    {
        var problemText = (request.TextPayload ?? string.Empty).Trim();
        if (modality == HomeworkHelpModalities.Text && problemText.Length == 0)
            return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null,
                "text_payload is required for text modality.");
        if (modality == HomeworkHelpModalities.Voice && request.VoiceCaptureId is null)
            return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null,
                "voice_capture_id is required for voice modality.");

        var submission = await _submissions.CreateTextOrVoiceAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            inputModality: modality,
            textPayload: modality == HomeworkHelpModalities.Text ? problemText : null,
            voiceCaptureId: request.VoiceCaptureId,
            ct: ct);

        var (redacted, _) = HomeworkTextRedactor.Redact(problemText);
        var upstream = await CallTutorRuntimeAsync(session, profile, language, redacted, request.SubjectId,
            extractedFromImage: false, ct);
        var response = MapUpstream(upstream, language, redacted, ocrAdapterBindingId: null);

        await _submissions.UpdateAfterProcessingAsync(
            submission: submission,
            extractedProblemText: null,
            ocrAdapterBindingId: null,
            aiRequestRecordId: TutorTextEndpoint.TryParseGuid(response.AiRequestRecordId),
            finalOutcome: response.FinalOutcome,
            response: response,
            ct: ct);

        return new HomeworkHelpResult(
            Outcome: response.FinalOutcome == "answered" ? HomeworkHelpOutcome.Answered : HomeworkHelpOutcome.Refused,
            Submission: submission,
            Response: response,
            Error: null);
    }

    private async Task<HomeworkHelpResult> HandleImageAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        HomeworkHelpSubmitRequest request,
        string language,
        Stream? imageStream,
        string? imageContentType,
        CancellationToken ct)
    {
        if (imageStream is null)
            return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null,
                "image_blob is required for image modality.");
        if (request.ImagePreprocessMetadata is null)
            return new HomeworkHelpResult(HomeworkHelpOutcome.InvalidRequest, null, null,
                "image_preprocess_metadata is required for image modality.");
        if (!request.ImagePreprocessMetadata.ExifStripped)
        {
            // Server defence-in-depth: client must confirm the strip happened.
            // We persist a refusal so audit can show the missing strip.
            var refusal = BuildRefusal(language, HomeworkHelpRefusalReasons.SafetyPii, aiRequestRecordId: string.Empty);
            var refusedSubmission = await _submissions.CreateTextOrVoiceAsync(
                tenantId: session.TenantId,
                studentSessionId: session.Id,
                inputModality: HomeworkHelpModalities.Image,
                textPayload: null,
                voiceCaptureId: null,
                ct: ct);
            await _submissions.UpdateAfterProcessingAsync(refusedSubmission, null, null, null,
                refusal.FinalOutcome, refusal, ct);
            return new HomeworkHelpResult(HomeworkHelpOutcome.Refused, refusedSubmission, refusal, null);
        }

        // Persist the image blob through the shared local-mode store.
        using var ms = new MemoryStream();
        await imageStream.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var blobRef = _submissions.PersistImageBlob(session.Id, bytes, imageContentType ?? "image/jpeg");

        var submission = await _submissions.CreateImageAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            imageBlobReference: blobRef,
            metadata: request.ImagePreprocessMetadata,
            ct: ct);

        // OCR pass.
        HomeworkOcrResult ocrResult;
        await using (var ocrStream = new MemoryStream(bytes, writable: false))
        {
            ocrResult = await _ocr.ExtractAsync(new HomeworkOcrRequest(
                ImageStream: ocrStream,
                ContentType: imageContentType ?? "image/jpeg",
                TutorLanguage: language,
                CorrelationId: request.CorrelationId == Guid.Empty ? session.CorrelationId : request.CorrelationId), ct);
        }

        if (ocrResult.Outcome == OcrOutcome.Unreadable)
        {
            var refusal = BuildRefusal(language, HomeworkHelpRefusalReasons.OcrUnreadable, aiRequestRecordId: string.Empty);
            await _submissions.UpdateAfterProcessingAsync(
                submission: submission,
                extractedProblemText: null,
                ocrAdapterBindingId: ocrResult.AdapterBindingId,
                aiRequestRecordId: null,
                finalOutcome: refusal.FinalOutcome,
                response: refusal,
                ct: ct);
            return new HomeworkHelpResult(HomeworkHelpOutcome.Refused, submission, refusal, null);
        }

        var (redacted, _) = HomeworkTextRedactor.Redact(ocrResult.ExtractedText);
        var upstream = await CallTutorRuntimeAsync(session, profile, language, redacted, request.SubjectId,
            extractedFromImage: true, ct);
        var response = MapUpstream(upstream, language, redacted, ocrResult.AdapterBindingId);

        await _submissions.UpdateAfterProcessingAsync(
            submission: submission,
            extractedProblemText: ocrResult.ExtractedText,
            ocrAdapterBindingId: ocrResult.AdapterBindingId,
            aiRequestRecordId: TutorTextEndpoint.TryParseGuid(response.AiRequestRecordId),
            finalOutcome: response.FinalOutcome,
            response: response,
            ct: ct);

        return new HomeworkHelpResult(
            Outcome: response.FinalOutcome == "answered" ? HomeworkHelpOutcome.Answered : HomeworkHelpOutcome.Refused,
            Submission: submission,
            Response: response,
            Error: null);
    }

    private async Task<UpstreamTutorResponse> CallTutorRuntimeAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        string language,
        string problemText,
        Guid subjectId,
        bool extractedFromImage,
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
                subject = subjectId == Guid.Empty
                    ? (session.ActiveSubjectId?.ToString() ?? string.Empty)
                    : subjectId.ToString(),
                active_lesson_id = session.ActiveLessonId?.ToString() ?? string.Empty,
                active_topic_id = session.ActiveTopicId?.ToString(),
                tutor_language = language,
                grade_band = "achievers",
                session_mode = "homework_help",
            },
            question = new
            {
                source = extractedFromImage ? "image_ocr" : "homework",
                text = problemText,
                non_solving_required = true,
            },
            submitted_at = DateTime.UtcNow,
            turn_number = 1,
        };

        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var response = await _tutorRuntime.AskAsync(stream, "application/json", ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return new UpstreamTutorResponse(response.IsSuccessStatusCode, raw);
    }

    public static HomeworkHelpSubmitResponse MapUpstream(
        UpstreamTutorResponse upstream,
        string language,
        string extractedText,
        Guid? ocrAdapterBindingId)
    {
        if (!upstream.IsSuccess || string.IsNullOrWhiteSpace(upstream.Body))
            return BuildRefusal(language, HomeworkHelpRefusalReasons.Grounding, aiRequestRecordId: string.Empty);

        try
        {
            using var doc = JsonDocument.Parse(upstream.Body);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("envelope_kind", out var ek) ? ek.GetString() : null;
            var recordId = root.TryGetProperty("routing_metadata", out var rm)
                && rm.TryGetProperty("record_id", out var rid)
                ? (rid.GetString() ?? string.Empty)
                : string.Empty;

            if (string.Equals(kind, "refusal", StringComparison.Ordinal))
            {
                var reasonRaw = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                var reason = MapRefusalReason(reasonRaw);
                return BuildRefusal(language, reason, recordId);
            }
            if (string.Equals(kind, "answer", StringComparison.Ordinal))
            {
                var answerText = root.TryGetProperty("answer_text", out var a) ? a.GetString() : null;
                var confidence = root.TryGetProperty("confidence_signal", out var c)
                    ? (c.GetString() ?? "high_confidence")
                    : "high_confidence";

                var evidence = new List<HomeworkEvidenceRefPayload>();
                if (root.TryGetProperty("evidence_refs", out var refs) && refs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in refs.EnumerateArray())
                    {
                        var chunkId = item.TryGetProperty("chunk_id", out var ci) ? ci.GetString() : null;
                        var sourceUri = item.TryGetProperty("source_lesson_id", out var sl)
                            ? sl.GetString()
                            : item.TryGetProperty("source_uri", out var su) ? su.GetString() : null;
                        if (!string.IsNullOrEmpty(chunkId))
                            evidence.Add(new HomeworkEvidenceRefPayload(chunkId!, sourceUri ?? "phase1://unknown"));
                    }
                }

                var steps = ParseSteps(root);

                return new HomeworkHelpSubmitResponse(
                    SubmissionId: Guid.Empty,
                    FinalOutcome: "answered",
                    ExtractedProblemText: string.IsNullOrEmpty(extractedText) ? null : extractedText,
                    AnswerTextAr: language == "ar" ? answerText : null,
                    AnswerTextEn: language == "en" ? answerText : null,
                    StepByStep: steps,
                    EvidenceRefs: evidence,
                    ConfidenceSignal: confidence,
                    AiRequestRecordId: recordId,
                    RefusalReason: null,
                    RefusalTextAr: null,
                    RefusalTextEn: null);
            }

            return BuildRefusal(language, HomeworkHelpRefusalReasons.Grounding, recordId);
        }
        catch (JsonException)
        {
            return BuildRefusal(language, HomeworkHelpRefusalReasons.Grounding, aiRequestRecordId: string.Empty);
        }
    }

    private static IReadOnlyList<HomeworkStepPayload> ParseSteps(JsonElement root)
    {
        var steps = new List<HomeworkStepPayload>();
        if (!root.TryGetProperty("step_by_step", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return steps;
        var idx = 0;
        foreach (var item in arr.EnumerateArray())
        {
            var textAr = item.TryGetProperty("text_ar", out var ta) ? (ta.GetString() ?? string.Empty) : string.Empty;
            var textEn = item.TryGetProperty("text_en", out var te) ? (te.GetString() ?? string.Empty) : string.Empty;
            var latex = item.TryGetProperty("latex", out var lx) ? lx.GetString() : null;
            steps.Add(new HomeworkStepPayload(idx++, textAr, textEn, latex));
        }
        return steps;
    }

    public static HomeworkHelpSubmitResponse BuildRefusal(string language, string reason, string aiRequestRecordId)
    {
        return new HomeworkHelpSubmitResponse(
            SubmissionId: Guid.Empty,
            FinalOutcome: "refused",
            ExtractedProblemText: null,
            AnswerTextAr: null,
            AnswerTextEn: null,
            StepByStep: Array.Empty<HomeworkStepPayload>(),
            EvidenceRefs: Array.Empty<HomeworkEvidenceRefPayload>(),
            ConfidenceSignal: "refused",
            AiRequestRecordId: aiRequestRecordId,
            RefusalReason: reason,
            RefusalTextAr: ResolveRefusalText(reason, "ar"),
            RefusalTextEn: ResolveRefusalText(reason, "en"));
    }

    public static string MapRefusalReason(string? upstreamReason)
    {
        var key = (upstreamReason ?? string.Empty).ToLowerInvariant();
        return key switch
        {
            "out_of_scope" => HomeworkHelpRefusalReasons.Scope,
            "scope" => HomeworkHelpRefusalReasons.Scope,
            "low_grounding_confidence" => HomeworkHelpRefusalReasons.Grounding,
            "grounding" => HomeworkHelpRefusalReasons.Grounding,
            "safety" => HomeworkHelpRefusalReasons.SafetyPii,
            "pii" => HomeworkHelpRefusalReasons.SafetyPii,
            "safety_pii" => HomeworkHelpRefusalReasons.SafetyPii,
            "ocr_unreadable" => HomeworkHelpRefusalReasons.OcrUnreadable,
            "plan_gate" => HomeworkHelpRefusalReasons.PlanGate,
            "direct_solution" => HomeworkHelpRefusalReasons.DirectSolution,
            "homework_direct_solution" => HomeworkHelpRefusalReasons.DirectSolution,
            _ => HomeworkHelpRefusalReasons.Grounding,
        };
    }

    public static string ResolveRefusalText(string reason, string locale) =>
        (reason, locale) switch
        {
            (HomeworkHelpRefusalReasons.Scope, "ar") => "السؤال خارج نطاق منهجك المعتمد.",
            (HomeworkHelpRefusalReasons.Scope, "en") => "This question is outside your approved curriculum.",
            (HomeworkHelpRefusalReasons.SafetyPii, "ar") => "لا يمكنني معالجة هذه الصورة لأنها قد تحتوي على بيانات حساسة.",
            (HomeworkHelpRefusalReasons.SafetyPii, "en") => "I can't process this image because it may contain sensitive data.",
            (HomeworkHelpRefusalReasons.Grounding, "ar") => "لم أجد مصدرًا موثوقًا في المنهج لشرح هذا السؤال.",
            (HomeworkHelpRefusalReasons.Grounding, "en") => "I couldn't find a trusted curriculum source to explain this.",
            (HomeworkHelpRefusalReasons.OcrUnreadable, "ar") => "لم أتمكن من قراءة الصورة. حاول التقاطها مرة أخرى أو اكتب السؤال يدويًا.",
            (HomeworkHelpRefusalReasons.OcrUnreadable, "en") => "I couldn't read the image. Try retaking it or type the problem manually.",
            (HomeworkHelpRefusalReasons.PlanGate, "ar") => "هذه الميزة متاحة على باقة أعلى.",
            (HomeworkHelpRefusalReasons.PlanGate, "en") => "This feature is available on a higher plan.",
            (HomeworkHelpRefusalReasons.DirectSolution, "ar") => "لن أحلّ الواجب نيابة عنك. سأشرح المفهوم وأقدّم مثالًا مختلفًا وتمارين مشابهة.",
            (HomeworkHelpRefusalReasons.DirectSolution, "en") => "I won't solve this homework for you. I'll explain the concept, give a different worked example, and similar practice items.",
            (_, "ar") => "لا أستطيع الإجابة على هذا السؤال الآن.",
            _ => "I can't answer that right now.",
        };
}
