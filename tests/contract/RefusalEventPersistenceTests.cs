using Muallimi.Application.AiOperations;
using Muallimi.Application.Audit;
using Muallimi.Domain.AiOperations;
using Muallimi.Infrastructure.AiOperations;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T056 (US2) — RefusalEvent persistence contract.
/// Verifies the consumer handler writes a RefusalEvent row linked to its
/// AiRequestRecord, is idempotent on event_id replay, and carries the
/// localised reason and tutor language unchanged from the ai-service
/// emitter.
/// </summary>
public class RefusalEventPersistenceTests
{
    [Fact]
    public async Task Consumer_Persists_RefusalEvent_To_Repository()
    {
        var repo = new FakeRefusalEventRepository();
        var handler = new RefusalEventPersistenceHandler(repo);

        var envelope = BuildEnvelope();
        await handler.HandleAsync(envelope);

        var row = Assert.Single(repo.Stored);
        Assert.Equal(envelope.EventId, row.EventId);
        Assert.Equal(envelope.RecordId, row.RecordId);
        Assert.Equal(envelope.Stage, row.Stage);
        Assert.Equal(envelope.ReasonCode, row.ReasonCode);
        Assert.Equal(envelope.LocalisedReason, row.LocalisedReason);
        Assert.Equal(envelope.TutorLanguage, row.TutorLanguage);
        Assert.Equal(envelope.OccurredAt, row.OccurredAt);
        Assert.Equal(1, repo.SaveCalls);
    }

    [Fact]
    public async Task Consumer_Is_Idempotent_On_Replay()
    {
        var repo = new FakeRefusalEventRepository();
        var handler = new RefusalEventPersistenceHandler(repo);

        var envelope = BuildEnvelope();
        await handler.HandleAsync(envelope);
        await handler.HandleAsync(envelope); // replay with same event_id

        Assert.Single(repo.Stored);
        Assert.Equal(1, repo.SaveCalls);
    }

    [Fact]
    public async Task Consumer_Supports_Arabic_And_English_Localised_Reasons()
    {
        var repo = new FakeRefusalEventRepository();
        var handler = new RefusalEventPersistenceHandler(repo);

        var arabic = BuildEnvelope(localisedReason: "السؤال خارج نطاق الدرس الحالي.", tutorLanguage: "ar");
        var english = BuildEnvelope(localisedReason: "That question is outside this lesson.", tutorLanguage: "en");
        await handler.HandleAsync(arabic);
        await handler.HandleAsync(english);

        Assert.Equal(2, repo.Stored.Count);
        Assert.Contains(repo.Stored, r => r.TutorLanguage == "ar" && r.LocalisedReason.Contains("خارج نطاق"));
        Assert.Contains(repo.Stored, r => r.TutorLanguage == "en" && r.LocalisedReason.Contains("outside this lesson"));
    }

    private static RefusalEventEnvelope BuildEnvelope(
        string localisedReason = "السؤال خارج نطاق الدرس الحالي.",
        string tutorLanguage = "ar")
        => new(
            EventId: Guid.NewGuid(),
            RecordId: Guid.NewGuid(),
            CorrelationId: "corr-1",
            SessionId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            Stage: "scope",
            ReasonCode: "scope.off_curriculum",
            LocalisedReason: localisedReason,
            TutorLanguage: tutorLanguage,
            FallbackLessonId: "lesson-1",
            OccurredAt: DateTime.UtcNow);

    private sealed class FakeRefusalEventRepository : IRefusalEventRepository
    {
        public List<RefusalEvent> Stored { get; } = new();
        public int SaveCalls { get; private set; }

        public Task<bool> ExistsAsync(Guid eventId, CancellationToken ct = default)
            => Task.FromResult(Stored.Any(r => r.EventId == eventId));

        public Task AddAsync(RefusalEvent row, CancellationToken ct = default)
        {
            Stored.Add(row);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCalls++;
            return Task.CompletedTask;
        }
    }
}
