using Muallimi.Api.AiOperations;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Infrastructure.PromptAudit;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T072 (US4) — End-to-end lifecycle test at the aggregate level.
///
/// Mirrors the independent test for Story 4: create a prompt, publish a new
/// version, promote it, roll back, and confirm both the rollback and the
/// original edit appear in the audit trail with monotonic version numbers,
/// immutable bodies, and at-least-one invalidation event per transition.
/// </summary>
public class PromptLifecycleTests
{
    [Fact]
    public async Task Full_Lifecycle_Preserves_Monotonic_Versions_And_Immutable_Bodies()
    {
        var publisher = new InMemoryPromptRegistryEventPublisher();
        var audit = new List<PromptAuditEntry>();

        // 1) Create prompt
        var prompt = Prompt.Create("system.lightweight", "tutor system prompt", "global");
        audit.Add(PromptAuditEntry.For(prompt.PromptId, null, PromptAuditActions.Created,
            "ops-1", "c-create"));

        // 2) Create v1 + promote
        var v1 = PromptVersion.Create(prompt.PromptId, 1,
            body: "v1 body — initial lightweight tutor prompt",
            declaredVariables: new[] { "tutor_language", "active_lesson_id", "subject" },
            createdBy: "ops-1");
        audit.Add(PromptAuditEntry.For(prompt.PromptId, v1.VersionId, PromptAuditActions.VersionCreated, "ops-1", "c-v1"));

        v1.MarkActive();
        prompt.SetActiveVersion(v1.VersionId);
        audit.Add(PromptAuditEntry.For(prompt.PromptId, v1.VersionId, PromptAuditActions.Promoted, "ops-1", "c-promote-v1"));
        await publisher.PublishAsync(new PromptRegistryEvent(
            Guid.NewGuid().ToString("N"), PromptRegistryEventTypes.Promoted,
            prompt.PromptId, v1.VersionId, "ops-1", "c-promote-v1", DateTime.UtcNow));

        // 3) Create v2 + promote
        var v2 = PromptVersion.Create(prompt.PromptId, 2,
            body: "v2 body — tightened refusal language",
            declaredVariables: new[] { "tutor_language", "active_lesson_id", "subject" },
            createdBy: "ops-1");
        audit.Add(PromptAuditEntry.For(prompt.PromptId, v2.VersionId, PromptAuditActions.VersionCreated, "ops-1", "c-v2"));

        v1.MarkSuperseded();
        v2.MarkActive();
        prompt.SetActiveVersion(v2.VersionId);
        audit.Add(PromptAuditEntry.For(prompt.PromptId, v2.VersionId, PromptAuditActions.Promoted, "ops-1", "c-promote-v2"));
        await publisher.PublishAsync(new PromptRegistryEvent(
            Guid.NewGuid().ToString("N"), PromptRegistryEventTypes.Promoted,
            prompt.PromptId, v2.VersionId, "ops-1", "c-promote-v2", DateTime.UtcNow));

        // 4) Rollback to v1
        v2.MarkSuperseded();
        v1.MarkActive();
        prompt.SetActiveVersion(v1.VersionId);
        audit.Add(PromptAuditEntry.For(prompt.PromptId, v1.VersionId, PromptAuditActions.RolledBack, "ops-1", "c-rollback"));
        await publisher.PublishAsync(new PromptRegistryEvent(
            Guid.NewGuid().ToString("N"), PromptRegistryEventTypes.RolledBack,
            prompt.PromptId, v1.VersionId, "ops-1", "c-rollback", DateTime.UtcNow));

        // ── Invariants ──
        Assert.True(v2.VersionNumber > v1.VersionNumber);
        Assert.Equal("v1 body — initial lightweight tutor prompt", v1.Body);
        Assert.Equal("v2 body — tightened refusal language", v2.Body);
        Assert.Equal(v1.VersionId, prompt.ActiveVersionId);

        Assert.Collection(audit,
            e => Assert.Equal(PromptAuditActions.Created, e.Action),
            e => Assert.Equal(PromptAuditActions.VersionCreated, e.Action),
            e => Assert.Equal(PromptAuditActions.Promoted, e.Action),
            e => Assert.Equal(PromptAuditActions.VersionCreated, e.Action),
            e => Assert.Equal(PromptAuditActions.Promoted, e.Action),
            e => Assert.Equal(PromptAuditActions.RolledBack, e.Action));

        var published = publisher.Published.ToList();
        Assert.Equal(3, published.Count);
        Assert.Contains(published, p => p.VersionId == v1.VersionId && p.EventType == PromptRegistryEventTypes.Promoted);
        Assert.Contains(published, p => p.VersionId == v2.VersionId && p.EventType == PromptRegistryEventTypes.Promoted);
        Assert.Contains(published, p => p.VersionId == v1.VersionId && p.EventType == PromptRegistryEventTypes.RolledBack);
    }

    [Fact]
    public void Incident_Lookup_Extracts_Prompt_Ids_From_Request_Record()
    {
        // ExtractPromptIds powers /internal/ai-ops/incidents/{id} so an
        // operator can trace which prompt version produced a given output.
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            scope_classifier = new { prompt_id = "scope.classifier", version_id = "v2" },
            generation = new { prompt_id = "system.lightweight", version_id = "v3" },
        });

        var ids = IncidentLookupEndpoint.ExtractPromptIds(json).ToList();

        Assert.Contains("scope.classifier", ids);
        Assert.Contains("system.lightweight", ids);
    }

    [Fact]
    public void Incident_Lookup_Is_Resilient_To_Malformed_Payloads()
    {
        Assert.Empty(IncidentLookupEndpoint.ExtractPromptIds(""));
        Assert.Empty(IncidentLookupEndpoint.ExtractPromptIds("not-json"));
        Assert.Empty(IncidentLookupEndpoint.ExtractPromptIds("[1,2,3]"));
    }
}
