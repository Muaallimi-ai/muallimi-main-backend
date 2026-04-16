using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T126 (Polish, FR-028) — Covers the pure retention-policy logic in
/// <see cref="RetentionPolicy"/> and the correlation-lookup contract in
/// <see cref="CorrelationLookupQuery"/>. The EF-backed enforcer is exercised
/// separately via integration tests against the DbContext; this file is the
/// unit-level contract.
/// </summary>
public class RetentionPolicyTests
{
    private static DateTime Utc(int year, int month, int day) =>
        new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Default_Policy_Has_90_Day_Investigation_Window()
    {
        Assert.Equal(TimeSpan.FromDays(90), RetentionPolicy.Default.InvestigationWindow);
    }

    [Fact]
    public void ComputeCutoff_Rejects_Non_Utc_Input()
    {
        var policy = RetentionPolicy.Default;
        var local = new DateTime(2026, 4, 16, 0, 0, 0, DateTimeKind.Local);
        Assert.Throws<ArgumentException>(() => policy.ComputeCutoff(local));
    }

    [Fact]
    public void ComputeCutoff_Returns_Now_Minus_Window()
    {
        var policy = new RetentionPolicy(TimeSpan.FromDays(30));
        var now = Utc(2026, 4, 16);
        var cutoff = policy.ComputeCutoff(now);
        Assert.Equal(Utc(2026, 3, 17), cutoff);
    }

    [Fact]
    public void IsExpired_On_AiRequestRecord_Uses_OccurredAt_Against_Cutoff()
    {
        var policy = new RetentionPolicy(TimeSpan.FromDays(10));
        var now = Utc(2026, 4, 16);
        var older = new AiRequestRecord { OccurredAt = Utc(2026, 4, 5) };
        var newer = new AiRequestRecord { OccurredAt = Utc(2026, 4, 10) };

        Assert.True(policy.IsExpired(older, now));
        Assert.False(policy.IsExpired(newer, now));
    }

    [Fact]
    public void IsExpired_On_RefusalEvent_Uses_OccurredAt_Against_Cutoff()
    {
        var policy = new RetentionPolicy(TimeSpan.FromDays(5));
        var now = Utc(2026, 4, 16);
        var expired = new RefusalEvent { OccurredAt = Utc(2026, 4, 10) };
        var retained = new RefusalEvent { OccurredAt = Utc(2026, 4, 12) };

        Assert.True(policy.IsExpired(expired, now));
        Assert.False(policy.IsExpired(retained, now));
    }

    [Fact]
    public void CorrelationLookupQuery_IsEmpty_When_No_Axis_Specified()
    {
        var q = new CorrelationLookupQuery();
        Assert.True(q.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => q.Validate());
    }

    [Theory]
    [InlineData("correlation")]
    [InlineData("session")]
    [InlineData("curriculum")]
    [InlineData("prompt_version")]
    [InlineData("guardrail")]
    public void CorrelationLookupQuery_Any_Single_FR028_Axis_Satisfies_Validation(string axis)
    {
        var q = axis switch
        {
            "correlation" => new CorrelationLookupQuery(CorrelationId: "corr-1"),
            "session" => new CorrelationLookupQuery(SessionId: Guid.NewGuid()),
            "curriculum" => new CorrelationLookupQuery(CurriculumType: "moe"),
            "prompt_version" => new CorrelationLookupQuery(PromptVersionId: "system.v3"),
            "guardrail" => new CorrelationLookupQuery(GuardrailOutcome: "scope.out_of_syllabus"),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

        Assert.False(q.IsEmpty);
        q.Validate(); // does not throw
    }

    [Fact]
    public void CorrelationLookupQuery_Validation_Rejects_Inverted_Time_Window()
    {
        var q = new CorrelationLookupQuery(
            CorrelationId: "corr-1",
            OccurredAfterUtc: Utc(2026, 4, 16),
            OccurredBeforeUtc: Utc(2026, 4, 10));

        Assert.Throws<InvalidOperationException>(() => q.Validate());
    }

    [Fact]
    public void RetentionPassResult_Empty_Reports_Zero_Purges()
    {
        var cutoff = Utc(2026, 1, 16);
        var empty = RetentionPassResult.Empty(cutoff);

        Assert.Equal(cutoff, empty.CutoffUtc);
        Assert.Equal(0, empty.AiRequestRecordsPurged);
        Assert.Equal(0, empty.RefusalEventsPurged);
    }
}
