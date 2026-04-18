using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Harness for US3 weekly report integration tests. Wires a
/// <see cref="WeeklyReportGenerator"/> against an in-memory
/// <see cref="MuallimiDbContext"/> with a scripted
/// <see cref="IPhase4TutorRuntimeClient"/> so the Phase 2 guardrail chain
/// pass-through can be asserted without spinning up the tutor runtime.
/// </summary>
internal sealed class WeeklyReportTestHarness
{
    public MuallimiDbContext Db { get; }
    public ScriptedTutorRuntimeClient Tutor { get; }
    public IWeeklyReportRepository Reports { get; }
    public IShareTokenValidator Tokens { get; }
    public WeeklyReportGenerator Generator { get; }

    public WeeklyReportTestHarness(MuallimiDbContext? db = null)
    {
        Db = db ?? Phase4TestDbContextFactory.Create();
        Tutor = new ScriptedTutorRuntimeClient();
        Reports = new WeeklyReportRepository(Db);
        var trails = new GuardrailDecisionTrailStore(Db);
        var aggregator = new WeeklyReportAggregator(Db);
        var summaries = new WeeklyReportSummaryGenerator(Tutor, trails);
        var outbox = new Phase4DownstreamEventOutbox(Db);
        var emitter = new WeeklyReportEventEmitter(outbox);
        Generator = new WeeklyReportGenerator(
            Db, Reports, aggregator, summaries, emitter,
            NullLogger<WeeklyReportGenerator>.Instance);
        Tokens = new ShareTokenValidator();
    }
}

internal sealed class ScriptedTutorRuntimeClient : IPhase4TutorRuntimeClient
{
    public List<Phase4GenerationRequest> Calls { get; } = new();
    public Phase4GenerationResult PassResult { get; set; } = new(
        Body: "نص تجريبي",
        GuardrailFinalStage: "pass",
        GuardrailChainOutput: "{\"stages\":[{\"name\":\"grounding\",\"verdict\":\"pass\"}]}",
        CorrelationId: string.Empty);

    public Func<Phase4GenerationRequest, Phase4GenerationResult>? ResultSelector { get; set; }

    public Task<Phase4GenerationResult> GenerateAsync(Phase4GenerationRequest request, CancellationToken ct = default)
    {
        Calls.Add(request);
        var result = ResultSelector is null
            ? PassResult with { Body = request.Language == "ar" ? "ملخص أسبوعي مُختبر" : "Tested weekly summary", CorrelationId = request.CorrelationId }
            : ResultSelector(request);
        return Task.FromResult(result);
    }
}
