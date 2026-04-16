using System.Text.Json;
using Muallimi.Api.AiOperations;
using Muallimi.Api.Tenancy;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T096 (US6) — AI operations query endpoints contract.
/// Filters must match the contract (<c>curriculum_type</c>, <c>grade</c>,
/// <c>subject</c>, <c>tutor_language</c>, <c>session_mode</c>), the aggregate
/// metric shape must surface volume / refusal_rate / cache_hit_rate /
/// grounded_answer_rate / per_branch / prompt_version_distribution, and the
/// role header must be operator or incident_investigation.
/// </summary>
public class AiOperationsQueryEndpointsTests
{
    [Fact]
    public void Filters_Apply_CurriculumType_Grade_Subject_TutorLanguage_SessionMode()
    {
        var records = new[]
        {
            Make(curriculum: "Moe", grade: "Grade7", subject: "Mathematics", lang: "Ar", mode: "Study"),
            Make(curriculum: "Moe", grade: "Grade7", subject: "Science", lang: "Ar", mode: "Study"),
            Make(curriculum: "International", grade: "Grade7", subject: "Mathematics", lang: "En", mode: "Homework"),
        };
        var query = records.AsQueryable();

        var filtered = AiOperationsEndpoints.ApplyFilters(query, "Moe", "Grade7", "Mathematics", "Ar", "Study", null).ToList();
        Assert.Single(filtered);
        Assert.Equal("Moe", filtered[0].CurriculumType);
        Assert.Equal("Mathematics", filtered[0].Subject);

        var englishOnly = AiOperationsEndpoints.ApplyFilters(query, null, null, null, "En", null, null).ToList();
        Assert.Single(englishOnly);
    }

    [Fact]
    public void Filters_Apply_FinalOutcome()
    {
        var records = new[]
        {
            Make(finalOutcome: "answered"),
            Make(finalOutcome: "refused"),
            Make(finalOutcome: "refused"),
            Make(finalOutcome: "fallback_redirect"),
        };

        var refused = AiOperationsEndpoints.ApplyFilters(records.AsQueryable(), null, null, null, null, null, "refused").ToList();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, r => Assert.Equal("refused", r.FinalOutcome));
    }

    [Fact]
    public void Aggregate_Metric_Shape_Matches_Contract()
    {
        var records = new[]
        {
            MakeAnswered(branch: "cache", inputTokens: 100, outputTokens: 0, latency: 40, grade: "Grade7"),
            MakeAnswered(branch: "llm_lightweight", inputTokens: 500, outputTokens: 200, latency: 800, grade: "Grade7"),
            MakeAnswered(branch: "llm_stronger", inputTokens: 1200, outputTokens: 400, latency: 1800, grade: "Grade7"),
            MakeRefused(stage: "scope", grade: "Grade7"),
            MakeFallback(grade: "Grade7"),
        };

        var aggregator = new MetricAggregator(new CostCalculator());
        var metric = aggregator.Aggregate(records, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        Assert.Equal(5, metric.Volume);
        Assert.Equal(1d / 5d, metric.RefusalRate, 6);
        Assert.Equal(1d / 5d, metric.CacheHitRate, 6);
        Assert.True(metric.GroundedAnswerRate > 0, "answered records should contribute to grounded rate");
        Assert.Contains("cache", metric.PerBranch.Keys);
        Assert.Contains("llm_lightweight", metric.PerBranch.Keys);
        Assert.Contains("llm_stronger", metric.PerBranch.Keys);
        Assert.Contains("refused", metric.PerBranch.Keys);
        Assert.Contains("grounding_fallback", metric.PerBranch.Keys);
    }

    [Fact]
    public void Aggregate_Serialises_PerBranch_And_PromptDistribution_As_Json()
    {
        var records = new[]
        {
            MakeAnsweredWithPrompts(
                branch: "llm_lightweight",
                prompts: new[] {
                    new[] { "stage", "prompt_id", "version_id" },
                    new[] { "generation", "system.lightweight", "v1" },
                }),
            MakeAnsweredWithPrompts(
                branch: "llm_lightweight",
                prompts: new[] {
                    new[] { "stage", "prompt_id", "version_id" },
                    new[] { "generation", "system.lightweight", "v2" },
                }),
        };

        var aggregator = new MetricAggregator(new CostCalculator());
        var metric = aggregator.Aggregate(records, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        var row = aggregator.BuildRow(metric, DateTime.UtcNow);

        // Volume + rates survive the round-trip
        Assert.Equal(2, row.Volume);
        Assert.False(string.IsNullOrWhiteSpace(row.PerBranch));
        Assert.False(string.IsNullOrWhiteSpace(row.PromptVersionDistribution));

        using var branchDoc = JsonDocument.Parse(row.PerBranch);
        Assert.True(branchDoc.RootElement.TryGetProperty("llm_lightweight", out _));

        using var promptsDoc = JsonDocument.Parse(row.PromptVersionDistribution);
        Assert.True(promptsDoc.RootElement.TryGetProperty("system.lightweight:v1", out var v1Count));
        Assert.Equal(1, v1Count.GetInt32());
        Assert.True(promptsDoc.RootElement.TryGetProperty("system.lightweight:v2", out var v2Count));
        Assert.Equal(1, v2Count.GetInt32());
    }

    [Fact]
    public void ComputeLatencyP95_Returns_95th_Percentile()
    {
        var records = Enumerable.Range(1, 100)
            .Select(i => Make(latency: i * 10))
            .ToList();

        var p95 = AiOperationsEndpoints.ComputeLatencyP95(records);
        // p95 across [10..1000] → index 94 → 950
        Assert.Equal(950, p95);
    }

    [Fact]
    public void Role_Gate_Requires_Operator_Or_IncidentInvestigation()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        // No role header → forbidden
        Assert.False(AiOperationsEndpoints.TryEnsureOperator(http, out var role, out var forbidden));
        Assert.Null(role);
        Assert.NotNull(forbidden);

        http.Request.Headers["X-Actor-Role"] = "student";
        Assert.False(AiOperationsEndpoints.TryEnsureOperator(http, out role, out forbidden));

        http.Request.Headers["X-Actor-Role"] = AiOperationsAuthorizationFilter.OperatorRole;
        Assert.True(AiOperationsEndpoints.TryEnsureOperator(http, out role, out forbidden));
        Assert.Equal(AiOperationsAuthorizationFilter.OperatorRole, role);
        Assert.Null(forbidden);

        http.Request.Headers["X-Actor-Role"] = AiOperationsAuthorizationFilter.IncidentInvestigationRole;
        Assert.True(AiOperationsEndpoints.TryEnsureOperator(http, out role, out forbidden));
        Assert.Equal(AiOperationsAuthorizationFilter.IncidentInvestigationRole, role);
    }

    // ── Helpers ──

    private static AiRequestRecord Make(
        string curriculum = "Moe",
        string grade = "Grade7",
        string subject = "Mathematics",
        string lang = "Ar",
        string mode = "Study",
        string finalOutcome = "answered",
        int latency = 100)
        => new()
        {
            RecordId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString("N"),
            SessionId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CurriculumType = curriculum,
            Grade = grade,
            Subject = subject,
            TutorLanguage = lang,
            SessionMode = mode,
            Stages = "[]",
            RoutingDecision = "{}",
            InputTokenCount = 0,
            OutputTokenCount = 0,
            LatencyMs = latency,
            CacheMatchScore = null,
            FinalOutcome = finalOutcome,
            QuestionTextPreview = "what is a fraction",
            PromptVersionsUsed = "[]",
            OccurredAt = DateTime.UtcNow,
        };

    private static AiRequestRecord MakeAnswered(string branch, int inputTokens, int outputTokens, int latency, string grade)
    {
        var record = Make(grade: grade, finalOutcome: "answered", latency: latency);
        record.RoutingDecision = JsonSerializer.Serialize(new { chosen_source = branch, model_tier = branch });
        record.InputTokenCount = inputTokens;
        record.OutputTokenCount = outputTokens;
        record.Stages = "[{\"stage\":\"grounding\",\"decision\":\"passed\"}]";
        return record;
    }

    private static AiRequestRecord MakeRefused(string stage, string grade)
    {
        var record = Make(grade: grade, finalOutcome: "refused");
        record.RoutingDecision = "{}";
        record.Stages = $"[{{\"stage\":\"{stage}\",\"decision\":\"refused\"}}]";
        return record;
    }

    private static AiRequestRecord MakeFallback(string grade)
    {
        var record = Make(grade: grade, finalOutcome: "fallback_redirect");
        record.RoutingDecision = JsonSerializer.Serialize(new { chosen_source = "grounding_fallback", model_tier = "grounding_fallback" });
        return record;
    }

    private static AiRequestRecord MakeAnsweredWithPrompts(string branch, string[][] prompts)
    {
        var record = MakeAnswered(branch, 200, 100, 300, "Grade7");
        var list = new List<object>();
        var header = prompts[0];
        for (var i = 1; i < prompts.Length; i++)
        {
            var row = prompts[i];
            var dict = new Dictionary<string, string>();
            for (var j = 0; j < header.Length; j++) dict[header[j]] = row[j];
            list.Add(dict);
        }
        record.PromptVersionsUsed = JsonSerializer.Serialize(list);
        return record;
    }
}
