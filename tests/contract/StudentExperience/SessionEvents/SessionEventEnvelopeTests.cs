using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;
using Muallimi.MainBackend.Tests.Contract.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.SessionEvents;

/// <summary>
/// T124 — Contract test for the Phase 3 → Phase 4 session event envelope.
///
/// Pins down the envelope shape (see
/// <c>specs/005-student-learning-experience/contracts/session-event-contract.md</c>)
/// and asserts every one of the eleven <see cref="SessionEventKind"/>
/// members round-trips through the outbox writer with its required payload
/// fields intact. A drift here — a missing field, a renamed key, a removed
/// kind — means Phase 4 consumers break on the first dispatch.
/// </summary>
public class SessionEventEnvelopeTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid CorrelationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void Envelope_Enumerates_Event_Kinds_In_Contract_Order()
    {
        // Phase 3 defined the first 11 kinds. Phase 5 adds `exam_answered`
        // at the end as an additive-only extension (exam submissions feed
        // into the same Phase 4 mastery pipeline through the Phase 3 event
        // transport). The additive-only rule from the Phase 4 downstream
        // contract applies to this enum as well: consumers MUST ignore
        // unknown kinds.
        var kinds = Enum.GetNames<SessionEventKind>();
        Assert.Equal(12, kinds.Length);
        Assert.Equal(new[]
        {
            "session_start",
            "lesson_view",
            "content_play",
            "question_asked",
            "answer_received",
            "refusal",
            "quiz_answered",
            "mock_test",
            "homework_help_used",
            "whiteboard_session",
            "session_end",
            "exam_answered",
        }, kinds);
    }

    public static IEnumerable<object[]> EveryEventKind()
    {
        yield return new object[]
        {
            SessionEventKind.session_start,
            (object)new { device_class = "mobile_small", preferred_language = "ar" },
            new[] { "device_class", "preferred_language" },
        };
        yield return new object[]
        {
            SessionEventKind.lesson_view,
            (object)new { lesson_id = Guid.NewGuid(), opened_from = "home" },
            new[] { "lesson_id", "opened_from" },
        };
        yield return new object[]
        {
            SessionEventKind.content_play,
            (object)new
            {
                lesson_id = Guid.NewGuid(),
                media_kind = "audio",
                started_at = DateTime.UtcNow,
                ended_at = (DateTime?)null,
                teacher_voice_profile_id = "teacher-voice-1",
            },
            new[] { "lesson_id", "media_kind", "started_at", "ended_at", "teacher_voice_profile_id" },
        };
        yield return new object[]
        {
            SessionEventKind.question_asked,
            (object)new { modality = "text", ai_request_record_id = Guid.NewGuid(), turn_number = 1 },
            new[] { "modality", "ai_request_record_id", "turn_number" },
        };
        yield return new object[]
        {
            SessionEventKind.answer_received,
            (object)new
            {
                ai_request_record_id = Guid.NewGuid(),
                final_outcome = "answered",
                confidence_signal = "high_confidence",
                evidence_ref_count = 3,
            },
            new[] { "ai_request_record_id", "final_outcome", "confidence_signal", "evidence_ref_count" },
        };
        yield return new object[]
        {
            SessionEventKind.refusal,
            (object)new { ai_request_record_id = Guid.NewGuid(), refusal_stage = "scope" },
            new[] { "ai_request_record_id", "refusal_stage" },
        };
        yield return new object[]
        {
            SessionEventKind.quiz_answered,
            (object)new { quiz_session_id = Guid.NewGuid(), question_id = Guid.NewGuid(), is_correct = true },
            new[] { "quiz_session_id", "question_id", "is_correct" },
        };
        yield return new object[]
        {
            SessionEventKind.mock_test,
            (object)new { mock_test_session_id = Guid.NewGuid(), state = "submitted", final_score = new { percent = 82 } },
            new[] { "mock_test_session_id", "state", "final_score" },
        };
        yield return new object[]
        {
            SessionEventKind.homework_help_used,
            (object)new { submission_id = Guid.NewGuid(), input_modality = "image", final_outcome = "answered" },
            new[] { "submission_id", "input_modality", "final_outcome" },
        };
        yield return new object[]
        {
            SessionEventKind.whiteboard_session,
            (object)new
            {
                whiteboard_session_id = Guid.NewGuid(),
                subject_id = Guid.NewGuid(),
                topic_id = Guid.NewGuid(),
                steps_played = 4,
                end_reason = "student_ended",
            },
            new[] { "whiteboard_session_id", "subject_id", "topic_id", "steps_played", "end_reason" },
        };
        yield return new object[]
        {
            SessionEventKind.session_end,
            (object)new { end_reason = "signed_out", duration_ms = 120_000 },
            new[] { "end_reason", "duration_ms" },
        };
    }

    [Theory]
    [MemberData(nameof(EveryEventKind))]
    public async Task Outbox_Writer_Serialises_Every_Kind_With_Required_Payload_Fields(
        SessionEventKind kind, object payload, string[] requiredPayloadKeys)
    {
        await using var db = NewInMemoryDb();
        var writer = new SessionEventOutboxWriter(db);

        var scope = new CurriculumScope(
            CurriculumType: "Moe",
            Grade: "Grade7",
            SubjectId: Guid.NewGuid(),
            ChapterId: Guid.NewGuid(),
            TopicId: Guid.NewGuid(),
            LessonId: Guid.NewGuid());

        var id = await writer.EnqueueAsync(
            kind, TenantId, SessionId, CorrelationId, payload, scope, "premium");
        await db.SaveChangesAsync();

        var row = await db.SessionEvents.IgnoreQueryFilters().SingleAsync(e => e.Id == id);

        // Envelope invariants — matches session-event-contract.md §Envelope.
        Assert.Equal(TenantId, row.TenantId);
        Assert.Equal(SessionId, row.StudentSessionId);
        Assert.Equal(CorrelationId, row.CorrelationId);
        Assert.Equal(kind.ToString(), row.EventKind);
        Assert.Equal("pending", row.DispatchState);
        Assert.Equal(0, row.DispatchAttempts);
        Assert.Null(row.DispatchedAt);
        Assert.Equal("premium", row.PlanTierSnapshot);

        using var payloadDoc = JsonDocument.Parse(row.EventPayload);
        foreach (var key in requiredPayloadKeys)
        {
            Assert.True(
                payloadDoc.RootElement.TryGetProperty(key, out _),
                $"payload for {kind} missing required key '{key}'");
        }

        using var scopeDoc = JsonDocument.Parse(row.CurriculumScope);
        foreach (var key in new[] { "curriculum_type", "grade", "subject_id", "chapter_id", "topic_id", "lesson_id" })
        {
            Assert.True(
                scopeDoc.RootElement.TryGetProperty(key, out _),
                $"curriculum_scope for {kind} missing required key '{key}'");
        }
    }

    [Fact]
    public async Task Payload_Is_Snake_Case_So_Phase4_Parsers_Do_Not_Drift()
    {
        await using var db = NewInMemoryDb();
        var writer = new SessionEventOutboxWriter(db);

        // Serializer converts PascalCase CLR property names to snake_case JSON —
        // Phase 4 consumers read snake_case per the contract.
        var id = await writer.EnqueueAsync(
            SessionEventKind.answer_received,
            TenantId, SessionId, CorrelationId,
            payload: new { AiRequestRecordId = Guid.NewGuid(), FinalOutcome = "answered", ConfidenceSignal = "cache_hit", EvidenceRefCount = 2 },
            curriculumScope: new CurriculumScope("Moe", "Grade7", null, null, null, null),
            planTierSnapshot: "free");
        await db.SaveChangesAsync();

        var row = await db.SessionEvents.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        using var doc = JsonDocument.Parse(row.EventPayload);
        Assert.True(doc.RootElement.TryGetProperty("ai_request_record_id", out _));
        Assert.True(doc.RootElement.TryGetProperty("final_outcome", out _));
        Assert.True(doc.RootElement.TryGetProperty("confidence_signal", out _));
        Assert.True(doc.RootElement.TryGetProperty("evidence_ref_count", out _));
        Assert.False(doc.RootElement.TryGetProperty("AiRequestRecordId", out _));
    }

    [Fact]
    public async Task Dispatch_State_Lifecycle_Is_Pending_Then_Published()
    {
        await using var db = NewInMemoryDb();
        var writer = new SessionEventOutboxWriter(db);

        var id = await writer.EnqueueAsync(
            SessionEventKind.session_end,
            TenantId, SessionId, CorrelationId,
            new { end_reason = "signed_out", duration_ms = 1000 },
            new CurriculumScope("Moe", "Grade7", null, null, null, null),
            "standard");
        await db.SaveChangesAsync();

        var row = await db.SessionEvents.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        Assert.Equal("pending", row.DispatchState);

        // Simulate a successful dispatch — the dispatcher path marks these
        // two fields together, so the contract test pins them together.
        row.DispatchState = "published";
        row.DispatchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var published = await db.SessionEvents.IgnoreQueryFilters().SingleAsync(e => e.Id == id);
        Assert.Equal("published", published.DispatchState);
        Assert.NotNull(published.DispatchedAt);
    }

    private static MuallimiDbContext NewInMemoryDb()
    {
        return Phase3TestDbContextFactory.Create(
            new StubTenantContextAccessor(TenantId),
            databaseName: $"session-events-{Guid.NewGuid():N}");
    }

    private sealed class StubTenantContextAccessor : IDbTenantContextAccessor
    {
        public StubTenantContextAccessor(Guid? tenantId) => CurrentTenantId = tenantId;
        public Guid? CurrentTenantId { get; }
    }
}
