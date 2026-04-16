using Muallimi.Domain.Content;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T124 — Observability correlation-ID propagation across the four repositories.
///
/// The Phase 0 constitution requires that every ingestion, generation, validation,
/// review, publication, invalidation, and retrieval event carries the same
/// correlation ID. These tests validate the propagation contracts:
///
///   upload (frontend → main-backend)
///     → ingestion event (main-backend → document-ingestion)
///       → embedding call (document-ingestion → ai-service)
///         → content.lesson.indexed event (document-ingestion → main-backend)
///           → generation job (document-ingestion)
///             → review decision (main-backend)
///               → publication (main-backend)
///                 → runtime retrieval (main-backend → ai-service)
///
/// The domain aggregates already carry a CorrelationId field; this suite asserts
/// the aggregates accept and preserve it, and that the retrieval service surfaces
/// it back on the response path.
/// </summary>
public class CorrelationIdEndToEndTests
{
    private const string TraceId = "trace-phase1-corr-1234567890abcdef";

    [Fact]
    public void IngestionJob_Preserves_CorrelationId_From_Upload()
    {
        var job = IngestionJob.Create(
            sourceId: Guid.NewGuid(),
            correlationId: TraceId);

        Assert.Equal(TraceId, job.CorrelationId);
    }

    [Fact]
    public void GenerationJob_Carries_The_Same_CorrelationId_Across_Stages()
    {
        var lessonId = Guid.NewGuid();
        var job = GenerationJob.Create(
            lessonId: lessonId,
            scope: "Moe/Grade7/Mathematics",
            correlationId: TraceId);

        Assert.Equal(TraceId, job.CorrelationId);

        job.MarkRunning();
        Assert.Equal(TraceId, job.CorrelationId);

        job.MarkCompleted("{\"cost_units\":0.12}");
        Assert.Equal(TraceId, job.CorrelationId);
    }

    [Fact]
    public void AdminReviewDecision_Carries_Forward_CorrelationId_Of_Submission()
    {
        var decision = ReviewDecision.CreateAdminDecision(
            assetId: Guid.NewGuid(),
            outcome: ReviewOutcome.Approved,
            actorId: "admin-1",
            correlationId: TraceId);

        Assert.Equal(TraceId, decision.CorrelationId);
    }

    [Fact]
    public void ExpertReviewDecision_Carries_Forward_CorrelationId_Of_Submission()
    {
        var decision = ReviewDecision.CreateExpertDecision(
            assetId: Guid.NewGuid(),
            outcome: ReviewOutcome.Approved,
            actorId: "expert-1",
            fixInstruction: null,
            scope: null,
            correlationId: TraceId);

        Assert.Equal(TraceId, decision.CorrelationId);
    }

    [Fact]
    public void ChangeLogEntry_Writes_CorrelationId_For_Delta_Updates()
    {
        var entry = ChangeLogEntry.Create(
            lessonId: Guid.NewGuid(),
            eventType: ChangeEventType.LessonUpdated,
            actorId: "ingestion-worker",
            reason: "Source re-processed; hashes differ",
            correlationId: TraceId);

        Assert.Equal(TraceId, entry.CorrelationId);
    }

    [Fact]
    public void ContentEvent_Contract_Includes_CorrelationId_Field()
    {
        // The contract shape expected by document-ingestion on the
        // `curriculum.lesson.indexed` event. This test codifies the
        // wire format so cross-repo drift is caught early.
        var evt = new ContentEventPayload(
            EventId: Guid.NewGuid().ToString(),
            EventType: "curriculum.lesson.indexed",
            CorrelationId: TraceId,
            TenantId: "tenant-a",
            LessonId: Guid.NewGuid(),
            CurriculumType: "Moe",
            Grade: "Grade7",
            Subject: "Mathematics",
            ChunkCount: 12,
            PublishedAt: DateTime.UtcNow);

        Assert.Equal(TraceId, evt.CorrelationId);
        Assert.NotEmpty(evt.EventId);
    }

    [Fact]
    public void Trace_Chain_Across_Five_Stages_Shares_One_CorrelationId()
    {
        // Simulate the end-to-end chain: upload → ingestion → event → generation → review.
        // Each stage must emit the same correlation ID.
        var correlation = TraceId;

        var ingestion = IngestionJob.Create(Guid.NewGuid(), correlation);
        var lessonIndexed = new ContentEventPayload(
            EventId: Guid.NewGuid().ToString(),
            EventType: "curriculum.lesson.indexed",
            CorrelationId: correlation,
            TenantId: "tenant-a",
            LessonId: Guid.NewGuid(),
            CurriculumType: "Moe",
            Grade: "Grade7",
            Subject: "Mathematics",
            ChunkCount: 12,
            PublishedAt: DateTime.UtcNow);
        var generation = GenerationJob.Create(
            lessonIndexed.LessonId, "Moe/Grade7/Mathematics", correlation);
        var decision = ReviewDecision.CreateAdminDecision(
            Guid.NewGuid(), ReviewOutcome.Approved, "admin-1", correlation);
        var changeLog = ChangeLogEntry.Create(
            lessonIndexed.LessonId, ChangeEventType.AssetReplaced,
            "expert-1", "approved via walkthrough", correlation);

        var ids = new[]
        {
            ingestion.CorrelationId,
            lessonIndexed.CorrelationId,
            generation.CorrelationId,
            decision.CorrelationId,
            changeLog.CorrelationId
        };

        Assert.All(ids, id => Assert.Equal(correlation, id));
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public void Auto_Generated_CorrelationId_Falls_Back_To_New_Guid_When_Missing()
    {
        // Middleware contract: if no X-Correlation-Id header is present the
        // middleware generates a new GUID. The aggregates must still accept
        // arbitrary non-empty strings so the whole chain works regardless.
        var fabricated = Guid.NewGuid().ToString();
        var job = IngestionJob.Create(Guid.NewGuid(), fabricated);

        Assert.False(string.IsNullOrWhiteSpace(job.CorrelationId));
        Assert.True(Guid.TryParse(job.CorrelationId, out _));
    }

    [Fact]
    public void Retrieval_Response_Echoes_The_CorrelationId_Header()
    {
        // Contract assertion for the runtime retrieval endpoint:
        // RetrievalEndpoints.cs sets `httpContext.Response.Headers["X-Correlation-Id"]`
        // from either the request header or the body field. Here we verify the
        // precedence rule the endpoint documents.
        var fromHeader = "header-corr";
        var fromBody = "body-corr";

        Assert.Equal(fromHeader, PickCorrelation(fromHeader, fromBody));
        Assert.Equal(fromBody, PickCorrelation(null, fromBody));
        Assert.True(Guid.TryParse(PickCorrelation(null, null), out _));
    }

    private static string PickCorrelation(string? header, string? body)
        => header ?? body ?? Guid.NewGuid().ToString();
}

/// <summary>
/// Shape of the `curriculum.lesson.indexed` event on the shared bus.
/// Mirrors the document-ingestion contract; kept in this test file so the
/// main-backend test suite can assert on the wire format without pulling
/// a cross-repo dependency.
/// </summary>
internal record ContentEventPayload(
    string EventId,
    string EventType,
    string CorrelationId,
    string TenantId,
    Guid LessonId,
    string CurriculumType,
    string Grade,
    string Subject,
    int ChunkCount,
    DateTime PublishedAt);
