using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T103 (US5) — Focus-area grounding invariant.
///
/// Every written <see cref="FocusArea"/> row MUST anchor to a Phase 1
/// curriculum node the student actually touched. The calculator honours
/// this by:
///   - only producing signals for <c>(subject, chapter, topic)</c> scopes
///     that appear on the student's ProgressRecord rows, and
///   - rejecting candidates whose deep link fails to resolve against the
///     Phase 1 retrieval surface.
///
/// These tests verify both arms of the invariant.
/// </summary>
public class FocusAreaGroundingTests
{
    [Fact]
    public async Task Every_Written_Focus_Area_Anchors_To_A_Node_The_Student_Touched()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        await harness.SeedQuizErrorAsync(tenantId, studentId, subjectId, chapterId, topicId, errorCount: 4);

        var result = await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-grounding-1");

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.WrittenCount);

        var rows = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        var row = Assert.Single(rows);
        Assert.Equal(subjectId, row.SubjectId);
        Assert.Equal(chapterId, row.ChapterId);
        Assert.Equal(topicId, row.TopicId);

        var touched = await harness.Db.ProgressRecords
            .IgnoreQueryFilters()
            .AnyAsync(p => p.TenantId == tenantId
                           && p.StudentId == studentId
                           && p.CurriculumScope.Contains(topicId.ToString()));
        Assert.True(touched);

        using var doc = JsonDocument.Parse(row.SuggestedNextStep);
        var deepLink = doc.RootElement.GetProperty("deep_link").GetString();
        Assert.NotNull(deepLink);
        Assert.Contains(subjectId.ToString(), deepLink);
        Assert.Contains(topicId.ToString(), deepLink);
    }

    [Fact]
    public async Task Deep_Link_Validator_Rejects_Unknown_Phase1_Nodes()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        harness.Curriculum.UnknownNodes.Add((subjectId, chapterId, topicId));
        await harness.SeedQuizErrorAsync(tenantId, studentId, subjectId, chapterId, topicId);

        var result = await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-grounding-2");

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.WrittenCount);
        Assert.Equal(1, result.RejectedByDeepLink);

        var rows = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Focus_Area_Write_Emits_Focus_Area_Updated_Downstream_Event()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        await harness.SeedQuizErrorAsync(tenantId, studentId, subjectId, chapterId, topicId);

        await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-grounding-3");

        var events = await harness.Db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                        && e.EventKind == Phase4DownstreamEventKind.focus_area_updated.ToString())
            .ToListAsync();
        Assert.Single(events);
        Assert.Equal(studentId, events[0].StudentId);
    }
}
