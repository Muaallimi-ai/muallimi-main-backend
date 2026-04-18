using System;
using System.Threading.Tasks;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T105 (US5) — Empty-list behaviour.
///
/// A student with no mastery gaps, no quiz errors, and no homework-help
/// signals MUST yield zero focus areas — the calculator never fabricates a
/// topic to fill the surface. Tutor runtime calls for zero candidates do
/// not fire; no downstream events are emitted.
/// </summary>
public class FocusAreaEmptyListTests
{
    [Fact]
    public async Task Student_With_No_Signals_Produces_Zero_Focus_Areas()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();

        var result = await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-empty-1");

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.WrittenCount);
        Assert.Empty(harness.Tutor.Calls);

        var rows = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Confident_Only_Student_Produces_Zero_Focus_Areas()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        await harness.SeedConfidentTopicAsync(tenantId, studentId, subjectId, chapterId, topicId);

        var result = await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-empty-2");

        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.WrittenCount);
        Assert.Empty(harness.Tutor.Calls);

        var rows = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Recompute_Removes_Stale_Rows_When_All_Signals_Clear()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        await harness.SeedQuizErrorAsync(tenantId, studentId, subjectId, chapterId, topicId);
        await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-empty-3a");

        var initial = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        Assert.Single(initial);

        foreach (var pr in harness.Db.ProgressRecords) harness.Db.ProgressRecords.Remove(pr);
        foreach (var ms in harness.Db.MasteryStates) harness.Db.MasteryStates.Remove(ms);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-empty-3b");
        Assert.Equal(0, result.WrittenCount);

        var after = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        Assert.Empty(after);
    }
}
