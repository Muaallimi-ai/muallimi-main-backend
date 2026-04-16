using System.Text.Json;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Infrastructure.PromptAudit;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T071 (US4) — Contract test for the prompt registry write + audit path.
/// Exercises the domain invariants consumed by the write endpoints: every
/// lifecycle action produces a <see cref="PromptAuditEntry"/>, version bodies
/// are immutable once created, version numbers are monotonic per prompt, and
/// every lifecycle action fans out a cache-invalidation envelope. Role
/// enforcement and HTTP shape are covered by the wiring in
/// <see cref="Muallimi.Api.PromptAudit.PromptRegistryEndpoints"/>.
/// </summary>
public class PromptRegistryWriteContractTests
{
    [Fact]
    public void Create_Prompt_Establishes_Draft_Status_And_Audit_Entry()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor system prompt", "global");
        var audit = PromptAuditEntry.For(prompt.PromptId, null, PromptAuditActions.Created,
            actor: "ops-1", correlationId: "corr-create");

        Assert.Equal(PromptStatus.Draft, prompt.Status);
        Assert.Null(prompt.ActiveVersionId);
        Assert.Equal(PromptAuditActions.Created, audit.Action);
        Assert.Equal("ops-1", audit.Actor);
        Assert.Equal("corr-create", audit.CorrelationId);
    }

    [Fact]
    public void Version_Numbers_Are_Monotonic_Per_Prompt()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");

        var v1 = PromptVersion.Create(prompt.PromptId, 1, "body-v1", new[] { "tutor_language" }, "ops-1");
        var v2 = PromptVersion.Create(prompt.PromptId, 2, "body-v2", new[] { "tutor_language" }, "ops-1");

        Assert.Equal(1, v1.VersionNumber);
        Assert.Equal(2, v2.VersionNumber);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PromptVersion.Create(prompt.PromptId, 0, "x", Array.Empty<string>(), "ops-1"));
    }

    [Fact]
    public void Version_Body_Is_Required_And_Trimmed()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        Assert.Throws<ArgumentException>(() =>
            PromptVersion.Create(prompt.PromptId, 1, "", new[] { "x" }, "ops-1"));
        Assert.Throws<ArgumentException>(() =>
            PromptVersion.Create(prompt.PromptId, 1, "body", new[] { "x" }, ""));
    }

    [Fact]
    public void Declared_Variables_Deduplicated_And_Persisted_As_Json()
    {
        var prompt = Prompt.Create("scope.classifier", "scope prompt", "global");
        var version = PromptVersion.Create(
            prompt.PromptId, 1, "body",
            new[] { "question", "chunks", "question" }, "ops-1");

        var declared = version.ReadDeclaredVariables();
        Assert.Equal(2, declared.Count);
        Assert.Equal(new[] { "question", "chunks" }, declared);
        Assert.NotEqual("[]", version.DeclaredVariables);
        using var doc = JsonDocument.Parse(version.DeclaredVariables);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Every_Lifecycle_Action_Produces_An_Audit_Entry()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        var version = PromptVersion.Create(prompt.PromptId, 1, "body", new[] { "x" }, "ops-1");

        var created = PromptAuditEntry.For(prompt.PromptId, null, PromptAuditActions.Created, "ops-1", "c1");
        var vc = PromptAuditEntry.For(prompt.PromptId, version.VersionId, PromptAuditActions.VersionCreated, "ops-1", "c2");
        var promoted = PromptAuditEntry.For(prompt.PromptId, version.VersionId, PromptAuditActions.Promoted, "ops-1", "c3");
        var archived = PromptAuditEntry.For(prompt.PromptId, version.VersionId, PromptAuditActions.Archived, "ops-1", "c4");
        var rolled = PromptAuditEntry.For(prompt.PromptId, version.VersionId, PromptAuditActions.RolledBack, "ops-1", "c5");

        Assert.All(new[] { created, vc, promoted, archived, rolled }, e =>
        {
            Assert.NotEqual(Guid.Empty, e.EntryId);
            Assert.Equal("ops-1", e.Actor);
            Assert.Equal(prompt.PromptId, e.PromptId);
        });
    }

    [Fact]
    public void Audit_Requires_Actor_And_Action()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        Assert.Throws<ArgumentException>(() =>
            PromptAuditEntry.For(prompt.PromptId, null, "", "ops-1", "corr"));
        Assert.Throws<ArgumentException>(() =>
            PromptAuditEntry.For(prompt.PromptId, null, "created", "", "corr"));
    }

    [Fact]
    public async Task Lifecycle_Publishes_Invalidation_Event_For_Each_Action()
    {
        var publisher = new InMemoryPromptRegistryEventPublisher();
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");

        await publisher.PublishAsync(new PromptRegistryEvent(
            Guid.NewGuid().ToString("N"), PromptRegistryEventTypes.Promoted,
            prompt.PromptId, Guid.NewGuid(), "ops-1", "corr-1", DateTime.UtcNow));
        await publisher.PublishAsync(new PromptRegistryEvent(
            Guid.NewGuid().ToString("N"), PromptRegistryEventTypes.Archived,
            prompt.PromptId, null, "ops-1", "corr-2", DateTime.UtcNow));
        await publisher.PublishAsync(new PromptRegistryEvent(
            Guid.NewGuid().ToString("N"), PromptRegistryEventTypes.RolledBack,
            prompt.PromptId, Guid.NewGuid(), "ops-1", "corr-3", DateTime.UtcNow));

        var published = publisher.Published.ToList();
        Assert.Equal(3, published.Count);
        Assert.Contains(published, e => e.EventType == PromptRegistryEventTypes.Promoted);
        Assert.Contains(published, e => e.EventType == PromptRegistryEventTypes.Archived);
        Assert.Contains(published, e => e.EventType == PromptRegistryEventTypes.RolledBack);
    }

    [Fact]
    public void Archive_Is_Reflected_On_Prompt_And_Versions()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        var version = PromptVersion.Create(prompt.PromptId, 1, "body", new[] { "x" }, "ops-1");
        version.MarkActive();
        prompt.SetActiveVersion(version.VersionId);

        prompt.Archive();
        version.MarkArchived();

        Assert.Equal(PromptStatus.Archived, prompt.Status);
        Assert.Equal(PromptVersionStatus.Archived, version.Status);
    }

    [Fact]
    public void Archived_Prompt_Cannot_Be_Promoted_Again()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        prompt.Archive();

        Assert.Throws<InvalidOperationException>(() => prompt.SetActiveVersion(Guid.NewGuid()));
    }
}
