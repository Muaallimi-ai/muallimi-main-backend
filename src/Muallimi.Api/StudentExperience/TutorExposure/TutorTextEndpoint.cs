using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// T060 (US3) — POST /student/tutor/text.
///
/// Student-facing SSE facade for text tutor chat. The handler:
///   1. Resolves tenant + session scope from headers (same pattern as US1 / US2).
///   2. Persists the student turn and writes a <c>question_asked</c> outbox row.
///   3. Calls the Phase 2 tutor runtime (<see cref="ITutorRuntimeClient.AskAsync"/>)
///      with the session scope, forwarding correlation / tenant / session headers.
///   4. Streams the upstream answer or refusal back as SSE events
///      (<c>delta</c> → <c>evidence</c> → <c>confidence</c> → <c>final</c>).
///   5. Persists the tutor turn linked to the Phase 2 <c>AiRequestRecord</c>
///      and writes <c>answer_received</c> or <c>refusal</c> outbox rows (T062).
///
/// The facade never generates tokens locally; every invariant in the
/// student-tutor-chat contract is enforced by re-emitting what the Phase 2
/// runtime produced.
/// </summary>
public static class TutorTextEndpoint
{
    public const string Route = "/api/student/tutor/text";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapTutorTextChat(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .WithName("StudentTutorTextChat")
            .WithTags("StudentExperience");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        TutorTextRequest request,
        IStudentSessionRepository sessions,
        ITutorRuntimeClient tutorRuntime,
        ITutorChatMessageRepository chatMessages,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var session = await sessions.FindAsync(request.SessionId, ct);
        if (session is null || session.TenantId != tenantId)
            return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId)
            return Results.NotFound();

        var language = NormaliseLanguage(request.TutorLanguage, session.TutorLanguage);
        var questionText = (request.QuestionText ?? string.Empty).Trim();
        if (questionText.Length == 0)
            return Results.BadRequest(new { error = "question_text is required." });

        var turnNumber = request.TurnNumber > 0
            ? request.TurnNumber
            : await chatMessages.NextTurnNumberAsync(session.Id, ct);

        await chatMessages.AppendStudentTurnAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            turnNumber: turnNumber,
            language: language,
            questionText: questionText,
            ct: ct);

        await outbox.EnqueueAsync(
            kind: SessionEventKind.question_asked,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                turn_number = turnNumber,
                modality = "text",
                tutor_language = language,
            },
            curriculumScope: ScopeFor(session, profile),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);

        await db.SaveChangesAsync(ct);

        var upstream = await CallTutorRuntimeAsync(tutorRuntime, session, profile, language,
            questionText, turnNumber, ct);

        var result = MapUpstream(upstream, language);

        await chatMessages.AppendTutorTurnAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            turnNumber: turnNumber,
            language: language,
            answerText: result.AnswerText,
            aiRequestRecordId: TryParseGuid(result.Final.AiRequestRecordId),
            guardrailFinalStage: result.Final.GuardrailFinalStage,
            finalOutcome: result.Final.FinalOutcome,
            confidenceSignal: result.Confidence.ConfidenceSignal,
            evidenceRefs: result.EvidenceRefs,
            ct: ct);

        await outbox.EnqueueAsync(
            kind: IsRefusal(result.Final) ? SessionEventKind.refusal : SessionEventKind.answer_received,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                turn_number = turnNumber,
                final_outcome = result.Final.FinalOutcome,
                confidence_signal = result.Confidence.ConfidenceSignal,
                ai_request_record_id = result.Final.AiRequestRecordId,
                guardrail_final_stage = result.Final.GuardrailFinalStage,
            },
            curriculumScope: ScopeFor(session, profile),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);

        await db.SaveChangesAsync(ct);

        PrepareSseResponse(http, session.CorrelationId);
        await WriteSseAsync(http.Response, result, ct);
        return Results.Empty;
    }

    // ── SSE helpers ─────────────────────────────────────────────────────

    public static void PrepareSseResponse(HttpContext http, Guid correlationId)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();
    }

    public static async Task WriteSseAsync(
        HttpResponse response, TutorTextStreamResult result, CancellationToken ct)
    {
        foreach (var delta in result.Deltas)
        {
            await WriteEventAsync(response, TutorTextSseEvents.Delta,
                new { delta_text = delta }, ct);
        }

        await WriteEventAsync(response, TutorTextSseEvents.Evidence,
            new { evidence_refs = result.EvidenceRefs }, ct);

        await WriteEventAsync(response, TutorTextSseEvents.Confidence,
            result.Confidence, ct);

        await WriteEventAsync(response, TutorTextSseEvents.Final, result.Final, ct);
        await response.Body.FlushAsync(ct);
    }

    public static async Task WriteEventAsync(HttpResponse response, string eventName, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, SerializerOptions);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(frame);
        await response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
        await response.Body.FlushAsync(ct);
    }

    public static IEnumerable<string> ChunkAnswer(string? answerText, int size = 120)
    {
        if (string.IsNullOrEmpty(answerText)) yield break;
        for (var i = 0; i < answerText.Length; i += size)
        {
            yield return answerText.Substring(i, Math.Min(size, answerText.Length - i));
        }
    }

    public static bool IsRefusal(TutorTextFinalPayload final) =>
        string.Equals(final.FinalOutcome, "refused", StringComparison.Ordinal);

    // ── Upstream mapping ────────────────────────────────────────────────

    public static async Task<UpstreamTutorResponse> CallTutorRuntimeAsync(
        ITutorRuntimeClient runtime,
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        string language,
        string questionText,
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
                session_mode = "tutor_chat",
            },
            question = new
            {
                source = "typed",
                text = questionText,
            },
            submitted_at = DateTime.UtcNow,
            turn_number = turnNumber,
        };

        var json = JsonSerializer.Serialize(body, SerializerOptions);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var response = await runtime.AskAsync(stream, "application/json", ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        return new UpstreamTutorResponse(response.IsSuccessStatusCode, raw);
    }

    public static TutorTextStreamResult MapUpstream(UpstreamTutorResponse upstream, string language)
    {
        if (!upstream.IsSuccess || string.IsNullOrWhiteSpace(upstream.Body))
        {
            return FallbackResult(language, "grounding_fallback",
                reason: "upstream_unavailable");
        }

        using var doc = JsonDocument.Parse(upstream.Body);
        var root = doc.RootElement;
        var kind = root.TryGetProperty("envelope_kind", out var ek)
            ? ek.GetString()
            : null;

        if (string.Equals(kind, "refusal", StringComparison.Ordinal))
            return MapRefusal(root, language);
        if (string.Equals(kind, "answer", StringComparison.Ordinal))
            return MapAnswer(root, language);

        // Unknown shape: fail closed with a grounding fallback.
        return FallbackResult(language, "unknown_envelope", reason: "upstream_shape");
    }

    private static TutorTextStreamResult MapAnswer(JsonElement root, string language)
    {
        var answerText = root.TryGetProperty("answer_text", out var a) ? a.GetString() : null;
        var confidence = root.TryGetProperty("confidence_signal", out var c)
            ? (c.GetString() ?? "high_confidence")
            : "high_confidence";
        if (!TutorTextSseEvents.AllowedConfidenceSignals.Contains(confidence))
            confidence = "low_confidence";

        var evidence = new List<TutorTextEvidenceRef>();
        if (root.TryGetProperty("evidence_refs", out var refs) && refs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in refs.EnumerateArray())
            {
                var chunkId = item.TryGetProperty("chunk_id", out var ci) ? ci.GetString() : null;
                var sourceUri = item.TryGetProperty("source_lesson_id", out var sl)
                    ? sl.GetString()
                    : item.TryGetProperty("source_uri", out var su) ? su.GetString() : null;
                if (!string.IsNullOrEmpty(chunkId))
                    evidence.Add(new TutorTextEvidenceRef(chunkId!, sourceUri ?? "phase1://unknown"));
            }
        }

        var recordId = root.TryGetProperty("routing_metadata", out var rm)
            && rm.TryGetProperty("record_id", out var rid)
            ? rid.GetString()
            : null;

        var outcome = string.Equals(confidence, "cache_hit", StringComparison.Ordinal)
            || confidence is "high_confidence" or "low_confidence"
            ? "answered"
            : "fallback";

        var final = new TutorTextFinalPayload(
            FinalOutcome: outcome,
            GuardrailFinalStage: "post_generation_grounding",
            AiRequestRecordId: recordId ?? string.Empty,
            RefusalTextAr: null,
            RefusalTextEn: null);

        return new TutorTextStreamResult(
            Deltas: ChunkAnswer(answerText).ToList(),
            EvidenceRefs: evidence,
            Confidence: new TutorTextConfidencePayload(confidence),
            Final: final,
            AnswerText: answerText);
    }

    private static TutorTextStreamResult MapRefusal(JsonElement root, string language)
    {
        var stage = root.TryGetProperty("stage", out var s) ? s.GetString() : "refusal";
        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "refused";
        var localised = root.TryGetProperty("reason_localised", out var rl) ? rl.GetString() : null;
        var recordId = root.TryGetProperty("routing_metadata", out var rm)
            && rm.TryGetProperty("record_id", out var rid)
            ? rid.GetString()
            : null;

        var refusalAr = ResolveRefusalText(reason, "ar", language == "ar" ? localised : null);
        var refusalEn = ResolveRefusalText(reason, "en", language == "en" ? localised : null);

        var final = new TutorTextFinalPayload(
            FinalOutcome: "refused",
            GuardrailFinalStage: stage ?? "refusal",
            AiRequestRecordId: recordId ?? string.Empty,
            RefusalTextAr: refusalAr,
            RefusalTextEn: refusalEn);

        return new TutorTextStreamResult(
            Deltas: Array.Empty<string>(),
            EvidenceRefs: Array.Empty<TutorTextEvidenceRef>(),
            Confidence: new TutorTextConfidencePayload("refused"),
            Final: final,
            AnswerText: null);
    }

    private static TutorTextStreamResult FallbackResult(string language, string stage, string reason)
    {
        var final = new TutorTextFinalPayload(
            FinalOutcome: "refused",
            GuardrailFinalStage: stage,
            AiRequestRecordId: string.Empty,
            RefusalTextAr: ResolveRefusalText(reason, "ar", null),
            RefusalTextEn: ResolveRefusalText(reason, "en", null));

        return new TutorTextStreamResult(
            Deltas: Array.Empty<string>(),
            EvidenceRefs: Array.Empty<TutorTextEvidenceRef>(),
            Confidence: new TutorTextConfidencePayload("refused"),
            Final: final,
            AnswerText: null);
    }

    public static string ResolveRefusalText(string? reason, string locale, string? upstreamLocalised)
    {
        if (!string.IsNullOrWhiteSpace(upstreamLocalised)) return upstreamLocalised!;
        var key = (reason ?? "refused").ToLowerInvariant();
        return (key, locale) switch
        {
            ("out_of_scope", "ar") => "هذا السؤال خارج نطاق درسك المعتمد.",
            ("out_of_scope", "en") => "This question is outside your approved lesson.",
            ("low_grounding_confidence", "ar") => "لم أجد مصدرًا موثوقًا لإجابة هذا السؤال.",
            ("low_grounding_confidence", "en") => "I couldn't find a trusted source to answer that.",
            ("safety", "ar") => "لا يمكنني الإجابة على هذا السؤال.",
            ("safety", "en") => "I can't answer that question.",
            ("upstream_unavailable", "ar") => "تعذّر الوصول إلى المعلم الذكي الآن. حاول مرة أخرى.",
            ("upstream_unavailable", "en") => "The AI tutor is temporarily unavailable. Please try again.",
            (_, "ar") => "لا أستطيع الإجابة على هذا السؤال الآن.",
            _ => "I can't answer that right now.",
        };
    }

    public static Guid? TryParseGuid(string? raw) =>
        Guid.TryParse(raw, out var parsed) ? parsed : null;

    public static string NormaliseLanguage(string? requested, string sessionDefault)
    {
        var normalised = requested?.Trim().ToLowerInvariant();
        if (normalised is "ar" or "en") return normalised;
        return sessionDefault is "ar" or "en" ? sessionDefault : "ar";
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

public sealed record UpstreamTutorResponse(bool IsSuccess, string Body);

public sealed record TutorTextStreamResult(
    IReadOnlyList<string> Deltas,
    IReadOnlyList<TutorTextEvidenceRef> EvidenceRefs,
    TutorTextConfidencePayload Confidence,
    TutorTextFinalPayload Final,
    string? AnswerText);

public static class TutorTextEndpointServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3TutorTextEndpoint(this IServiceCollection services)
    {
        services.AddScoped<ITutorChatMessageRepository, TutorChatMessageRepository>();
        return services;
    }
}
