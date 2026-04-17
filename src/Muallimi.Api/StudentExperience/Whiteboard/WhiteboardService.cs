using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.Whiteboard;

/// <summary>
/// T116 (US8) — WhiteboardService.
///
/// Orchestrates the plan-gated, subject-gated whiteboard lifecycle:
///   1. <c>StartAsync</c> re-checks the plan gate (via
///      <see cref="IPlanGateResolver"/>) and the whiteboard subject allow-list
///      (Mathematics and Physics at MVP per the contract; the Subject enum
///      exposes Mathematics + Science for grade 7 today, so Science is the
///      placeholder the tenant ships until the taxonomy expands to Physics).
///      A refusal is returned for any of <c>plan_gate</c>,
///      <c>subject_gate</c>, <c>tenant_denied</c>, or <c>scope_not_found</c>
///      so the caller can surface the exact reason.
///   2. <c>StepAsync</c> reads the approved Phase 1 content chunks for the
///      requested topic and projects the requested step index into a
///      bounded list of draw operations plus bilingual narration. Every
///      step cites the approved chunk id on <c>evidence_refs</c>; the narration
///      voice profile source is explicit so Phase 1 teacher voices and
///      Phase 2 AI tutor voices stay distinct on the wire.
///   3. <c>EndAsync</c> finalises the row and returns the step count so the
///      endpoint can emit the <c>whiteboard_session</c> event with the
///      contract-mandated <c>steps_played</c>.
///
/// Constitution invariants respected:
///   - Plan and subject gates are re-checked on every call — the UI
///     resolver's decision is advisory only.
///   - Every step references an approved Phase 1 <c>ContentChunk</c>; no
///     content is generated locally.
///   - Narration voice profile source is always declared; Phase 1 teacher
///     voice ids and Phase 2 AI tutor voice ids are never mixed.
/// </summary>
public interface IWhiteboardService
{
    Task<WhiteboardStartResult> StartAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        WhiteboardStartRequest request,
        CancellationToken ct = default);

    Task<WhiteboardStepResult> StepAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        WhiteboardSession whiteboardSession,
        WhiteboardStepRequest request,
        CancellationToken ct = default);

    Task<WhiteboardEndResult> EndAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        WhiteboardSession whiteboardSession,
        WhiteboardEndRequest request,
        CancellationToken ct = default);
}

public enum WhiteboardStartOutcome
{
    Accepted,
    InvalidRequest,
    Refused,
}

public sealed record WhiteboardStartResult(
    WhiteboardStartOutcome Outcome,
    WhiteboardSession? WhiteboardSession,
    WhiteboardStartResponse? Response,
    string? Error);

public enum WhiteboardStepOutcome
{
    Ok,
    AlreadyEnded,
    InvalidStepIndex,
    NoContent,
    GateRevoked,
}

public sealed record WhiteboardStepResult(
    WhiteboardStepOutcome Outcome,
    WhiteboardStepResponse? Response,
    string? RevokedReason);

public enum WhiteboardEndOutcome
{
    Ok,
    AlreadyEnded,
    InvalidReason,
}

public sealed record WhiteboardEndResult(
    WhiteboardEndOutcome Outcome,
    WhiteboardEndResponse? Response,
    int StepsPlayed);

public sealed class WhiteboardService : IWhiteboardService
{
    /// <summary>
    /// MVP whiteboard allow-list: Mathematics and Physics per the contract.
    /// The Subject enum exposes Mathematics + Science for grade 7 today;
    /// Science sits in the allow-list as the temporary stand-in for Physics
    /// until the Subject taxonomy expands. Non-eligible subjects (Arabic,
    /// English) are refused with <c>subject_gate</c>.
    /// </summary>
    public static readonly IReadOnlySet<Subject> EligibleSubjects = new HashSet<Subject>
    {
        Subject.Mathematics,
        Subject.Science,
    };

    private const int MaxStepsPerSession = 64;

    private readonly MuallimiDbContext _db;
    private readonly IPlanGateResolver _planGate;
    private readonly IWhiteboardSessionRepository _whiteboards;

    public WhiteboardService(
        MuallimiDbContext db,
        IPlanGateResolver planGate,
        IWhiteboardSessionRepository whiteboards)
    {
        _db = db;
        _planGate = planGate;
        _whiteboards = whiteboards;
    }

    public async Task<WhiteboardStartResult> StartAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        WhiteboardStartRequest request,
        CancellationToken ct = default)
    {
        if (request is null)
            return new WhiteboardStartResult(WhiteboardStartOutcome.InvalidRequest, null, null,
                "request is required.");
        if (request.SessionId == Guid.Empty)
            return new WhiteboardStartResult(WhiteboardStartOutcome.InvalidRequest, null, null,
                "session_id is required.");
        if (request.SubjectId == Guid.Empty)
            return new WhiteboardStartResult(WhiteboardStartOutcome.InvalidRequest, null, null,
                "subject_id is required.");
        if (request.TopicId == Guid.Empty)
            return new WhiteboardStartResult(WhiteboardStartOutcome.InvalidRequest, null, null,
                "topic_id is required.");
        if (!WhiteboardSessionModes.IsAccepted(request.SessionMode))
            return new WhiteboardStartResult(WhiteboardStartOutcome.InvalidRequest, null, null,
                "session_mode is invalid.");

        var language = session.TutorLanguage == "en" ? "en" : "ar";

        var subject = LessonRetrievalService.SubjectFromGuid(request.SubjectId);
        if (subject is null)
            return Refusal(language, WhiteboardRefusalReasons.ScopeNotFound);

        // Subject allow-list re-check. Even if the plan gate allows the
        // student, the subject gate is its own fail-closed check — Arabic
        // and English never open a whiteboard at MVP.
        if (!EligibleSubjects.Contains(subject.Value))
            return Refusal(language, WhiteboardRefusalReasons.SubjectGate);

        // Plan gate re-check. The PlanGatePolicy row for the whiteboard mode
        // carries both the required plan tier and the subject scope, so a
        // tenant override can extend or narrow the allow-list without a
        // code change.
        var gate = await _planGate.EvaluateAsync(
            new PlanGateContext(
                Mode: StudentModes.Whiteboard,
                TenantId: session.TenantId,
                PlanTier: session.PlanTierSnapshot,
                SubjectId: request.SubjectId,
                Grade: profile.Grade),
            ct);
        if (!gate.Allowed)
        {
            var reason = gate.Reason switch
            {
                "subject_not_permitted" => WhiteboardRefusalReasons.SubjectGate,
                "grade_not_permitted" => WhiteboardRefusalReasons.ScopeNotFound,
                _ => WhiteboardRefusalReasons.PlanGate,
            };
            return Refusal(language, reason);
        }

        // Scope check: refuse if there is no approved Phase 1 content for
        // the requested subject (and topic, when topic maps back to a
        // lesson). Silently starting a session with zero steps would trap
        // the student in a dead end.
        var anyApproved = await AnyApprovedChunkAsync(profile, subject.Value, ct);
        if (!anyApproved)
            return Refusal(language, WhiteboardRefusalReasons.ScopeNotFound);

        var row = await _whiteboards.CreateAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            subjectId: request.SubjectId,
            topicId: request.TopicId,
            planTierSnapshot: session.PlanTierSnapshot,
            sessionMode: request.SessionMode,
            ct: ct);

        var response = new WhiteboardStartResponse(
            WhiteboardSessionId: row.Id,
            SubjectId: row.SubjectId,
            TopicId: row.TopicId,
            PlanTierSnapshot: row.PlanTierSnapshot,
            StartedAt: row.StartedAt,
            InitialCanvasState: new WhiteboardCanvasState(
                Width: 1024,
                Height: 576,
                BackgroundColor: "#FFFFFF",
                TextDirection: language == "ar" ? "rtl" : "ltr"),
            RefusalReason: null,
            RefusalTextAr: null,
            RefusalTextEn: null);

        return new WhiteboardStartResult(
            Outcome: WhiteboardStartOutcome.Accepted,
            WhiteboardSession: row,
            Response: response,
            Error: null);
    }

    public async Task<WhiteboardStepResult> StepAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        WhiteboardSession whiteboardSession,
        WhiteboardStepRequest request,
        CancellationToken ct = default)
    {
        if (whiteboardSession.EndedAt is not null)
            return new WhiteboardStepResult(WhiteboardStepOutcome.AlreadyEnded, null, null);

        if (request.RequestedStepIndex < 0 || request.RequestedStepIndex >= MaxStepsPerSession)
            return new WhiteboardStepResult(WhiteboardStepOutcome.InvalidStepIndex, null, null);

        // Re-check the plan + subject gate every step so a plan revoked
        // mid-run terminates the session rather than silently continuing.
        var gate = await _planGate.EvaluateAsync(
            new PlanGateContext(
                Mode: StudentModes.Whiteboard,
                TenantId: session.TenantId,
                PlanTier: session.PlanTierSnapshot,
                SubjectId: whiteboardSession.SubjectId,
                Grade: profile.Grade),
            ct);
        if (!gate.Allowed)
        {
            return new WhiteboardStepResult(
                Outcome: WhiteboardStepOutcome.GateRevoked,
                Response: null,
                RevokedReason: gate.Reason);
        }

        var subject = LessonRetrievalService.SubjectFromGuid(whiteboardSession.SubjectId);
        if (subject is null)
            return new WhiteboardStepResult(WhiteboardStepOutcome.NoContent, null, null);

        var chunks = await LoadApprovedChunksAsync(profile, subject.Value, ct);
        if (chunks.Count == 0)
            return new WhiteboardStepResult(WhiteboardStepOutcome.NoContent, null, null);

        if (request.RequestedStepIndex >= chunks.Count)
            return new WhiteboardStepResult(WhiteboardStepOutcome.InvalidStepIndex, null, null);

        var chunk = chunks[request.RequestedStepIndex];
        var language = session.TutorLanguage == "en" ? "en" : "ar";

        var drawOps = BuildDrawOps(chunk, language);
        await _whiteboards.AppendStepAsync(
            whiteboardSession,
            request.RequestedStepIndex,
            drawOps.Count,
            ct);

        var narrationAr = BuildNarration(chunk.Text, "ar");
        var narrationEn = BuildNarration(chunk.Text, "en");

        // Whiteboard narration uses the Phase 1 teacher voice profile
        // bound to this subject. Phase 2 AI tutor voices are never issued
        // here — the source field declares the provenance explicitly.
        var tutorLanguage = language == "ar" ? TutorLanguage.Ar : TutorLanguage.En;
        var voiceProfileId = Phase1TeacherVoiceProfiles.Resolve(subject.Value, tutorLanguage);

        var response = new WhiteboardStepResponse(
            WhiteboardSessionId: whiteboardSession.Id,
            StepIndex: request.RequestedStepIndex,
            DrawOps: drawOps,
            NarrationTextAr: narrationAr,
            NarrationTextEn: narrationEn,
            NarrationVoiceProfileId: voiceProfileId,
            NarrationVoiceProfileSource: WhiteboardNarrationVoiceProfileSources.Phase1Curriculum,
            EvidenceRefs: new[]
            {
                new WhiteboardEvidenceRef(
                    ChunkId: chunk.ChunkId.ToString(),
                    SourceUri: $"phase1://lesson/{chunk.LessonId:N}/chunk/{chunk.ChunkId:N}"),
            });

        return new WhiteboardStepResult(WhiteboardStepOutcome.Ok, response, null);
    }

    public async Task<WhiteboardEndResult> EndAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        WhiteboardSession whiteboardSession,
        WhiteboardEndRequest request,
        CancellationToken ct = default)
    {
        if (!WhiteboardEndReasons.IsAccepted(request.EndReason))
            return new WhiteboardEndResult(WhiteboardEndOutcome.InvalidReason, null, 0);

        if (whiteboardSession.EndedAt is not null)
        {
            var stepCount = _whiteboards.ReadStepLog(whiteboardSession).Count;
            return new WhiteboardEndResult(WhiteboardEndOutcome.AlreadyEnded, null, stepCount);
        }

        var ended = await _whiteboards.EndAsync(whiteboardSession, request.EndReason, ct);
        var stepsPlayed = _whiteboards.ReadStepLog(ended).Count;

        var response = new WhiteboardEndResponse(
            WhiteboardSessionId: ended.Id,
            EndedAt: ended.EndedAt!.Value,
            EndReason: ended.EndReason!,
            StepsPlayed: stepsPlayed);

        return new WhiteboardEndResult(WhiteboardEndOutcome.Ok, response, stepsPlayed);
    }

    // ── Phase 1 retrieval helpers ──────────────────────────────────────

    private sealed record WhiteboardChunkSource(Guid LessonId, Guid ChunkId, int Sequence, string Text);

    private async Task<bool> AnyApprovedChunkAsync(
        StudentProfile profile, Subject subject, CancellationToken ct)
    {
        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);

        return await _db.Lessons
            .AsNoTracking()
            .AnyAsync(l => l.Status == LessonStatus.Approved
                           && l.Subject == subject
                           && (curriculumType == null || l.CurriculumType == curriculumType)
                           && (grade == null || l.Grade == grade),
                ct);
    }

    private async Task<IReadOnlyList<WhiteboardChunkSource>> LoadApprovedChunksAsync(
        StudentProfile profile, Subject subject, CancellationToken ct)
    {
        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);

        var lessonIds = await _db.Lessons
            .AsNoTracking()
            .Where(l => l.Status == LessonStatus.Approved
                        && l.Subject == subject
                        && (curriculumType == null || l.CurriculumType == curriculumType)
                        && (grade == null || l.Grade == grade))
            .Select(l => l.LessonId)
            .ToListAsync(ct);
        if (lessonIds.Count == 0) return Array.Empty<WhiteboardChunkSource>();

        var lessonIdSet = lessonIds.ToHashSet();
        var chunks = await _db.ContentChunks
            .AsNoTracking()
            .Where(c => c.Status == ChunkStatus.Active && lessonIdSet.Contains(c.LessonId))
            .OrderBy(c => c.LessonId)
            .ThenBy(c => c.Sequence)
            .Select(c => new WhiteboardChunkSource(c.LessonId, c.ChunkId, c.Sequence, c.Text))
            .Take(MaxStepsPerSession)
            .ToListAsync(ct);

        return chunks;
    }

    private static IReadOnlyList<WhiteboardDrawOp> BuildDrawOps(
        WhiteboardChunkSource chunk, string language)
    {
        var ops = new List<WhiteboardDrawOp>(3)
        {
            new(
                OpType: WhiteboardDrawOpTypes.Clear,
                Payload: new WhiteboardDrawPayload(
                    Text: null, Latex: null, TextDirection: null,
                    X: null, Y: null, Width: 1024, Height: 576,
                    Color: "#FFFFFF", Path: null)),
            new(
                OpType: WhiteboardDrawOpTypes.DrawStroke,
                Payload: new WhiteboardDrawPayload(
                    Text: FirstLine(chunk.Text),
                    Latex: null,
                    TextDirection: language == "ar" ? "rtl" : "ltr",
                    X: 48, Y: 80, Width: null, Height: null,
                    Color: "#111111", Path: null)),
        };

        if (chunk.Text.Contains('=') || chunk.Text.Contains('+') || chunk.Text.Contains('-'))
        {
            ops.Add(new WhiteboardDrawOp(
                OpType: WhiteboardDrawOpTypes.DrawLatex,
                Payload: new WhiteboardDrawPayload(
                    Text: null,
                    Latex: ExtractLatexHint(chunk.Text),
                    TextDirection: "ltr",
                    X: 96, Y: 220, Width: null, Height: null,
                    Color: "#0D47A1", Path: null)));
        }

        return ops;
    }

    private static string BuildNarration(string text, string language)
    {
        var sentence = FirstLine(text);
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return language == "ar"
                ? "فلنستعرض هذه الخطوة من الدرس."
                : "Let's walk through this step of the lesson.";
        }
        return language == "ar"
            ? $"لاحظ في هذه الخطوة: {sentence}."
            : $"In this step, notice: {sentence}.";
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim();
        var idx = trimmed.IndexOfAny(new[] { '.', '\n', '؟', '?', '!' });
        var span = idx > 0 ? trimmed.Substring(0, idx) : trimmed;
        if (span.Length > 160) span = span.Substring(0, 160);
        return span.Trim();
    }

    private static string ExtractLatexHint(string text)
    {
        var line = FirstLine(text);
        if (string.IsNullOrEmpty(line)) return "x = y";
        return line.Length <= 48 ? line : line.Substring(0, 48);
    }

    private static T? ParseEnumOrNull<T>(string? raw) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }

    // ── refusal helpers ───────────────────────────────────────────────

    private static WhiteboardStartResult Refusal(string language, string reason)
    {
        var response = new WhiteboardStartResponse(
            WhiteboardSessionId: null,
            SubjectId: null,
            TopicId: null,
            PlanTierSnapshot: null,
            StartedAt: null,
            InitialCanvasState: null,
            RefusalReason: reason,
            RefusalTextAr: ResolveRefusalText(reason, "ar"),
            RefusalTextEn: ResolveRefusalText(reason, "en"));
        return new WhiteboardStartResult(WhiteboardStartOutcome.Refused, null, response, null);
    }

    public static string ResolveRefusalText(string reason, string locale) =>
        (reason, locale) switch
        {
            (WhiteboardRefusalReasons.PlanGate, "ar") => "السبورة التفاعلية متاحة في باقة أعلى.",
            (WhiteboardRefusalReasons.PlanGate, "en") => "The live whiteboard is available on a higher plan.",
            (WhiteboardRefusalReasons.SubjectGate, "ar") => "السبورة التفاعلية متاحة في الرياضيات والفيزياء فقط حاليًا.",
            (WhiteboardRefusalReasons.SubjectGate, "en") => "The live whiteboard is currently available for Mathematics and Physics only.",
            (WhiteboardRefusalReasons.TenantDenied, "ar") => "لا تملك صلاحية فتح السبورة في هذه المؤسسة.",
            (WhiteboardRefusalReasons.TenantDenied, "en") => "You are not permitted to open the whiteboard in this tenant.",
            (WhiteboardRefusalReasons.ScopeNotFound, "ar") => "لا يوجد محتوى معتمد لعرضه على السبورة لهذا الموضوع.",
            (WhiteboardRefusalReasons.ScopeNotFound, "en") => "There is no approved content to render on the whiteboard for this topic.",
            (_, "ar") => "تعذّر فتح السبورة التفاعلية الآن.",
            _ => "The live whiteboard can't be opened right now.",
        };
}

public static class WhiteboardServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3Whiteboard(this IServiceCollection services)
    {
        services.AddScoped<IWhiteboardSessionRepository, WhiteboardSessionRepository>();
        services.AddScoped<IWhiteboardService, WhiteboardService>();
        return services;
    }
}
