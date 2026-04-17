using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.HomeDashboard;

/// <summary>
/// T030 (US1) — HomeDashboardService.
///
/// Assembles the initial dashboard frame: Arabic + English greetings,
/// active scope, resume target (last mode transition, if any), recommended
/// topics (Phase 1 read-only), and per-mode tile states from
/// <see cref="IPlanGateResolver"/>. Called by <see cref="SessionStartEndpoint"/>
/// after the <c>StudentSession</c> row is created so every tile decision
/// reflects the same plan-tier snapshot that downstream mode-transition
/// checks use.
/// </summary>
public interface IHomeDashboardService
{
    Task<HomeDashboardState> BuildAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        CancellationToken ct = default);

    Task<IReadOnlyList<ModeTileState>> ResolveTilesAsync(
        Guid tenantId,
        string planTier,
        CancellationToken ct = default);
}

public sealed class HomeDashboardService : IHomeDashboardService
{
    private static readonly string[] OfferedModes =
    {
        StudentModes.Study, StudentModes.TutorChat, StudentModes.TutorVoice,
        StudentModes.SolveQuestions, StudentModes.MockTest,
        StudentModes.HomeworkHelp, StudentModes.Whiteboard,
    };

    private readonly IPlanGateResolver _planGate;
    private readonly MuallimiDbContext _db;

    public HomeDashboardService(IPlanGateResolver planGate, MuallimiDbContext db)
    {
        _planGate = planGate;
        _db = db;
    }

    public async Task<HomeDashboardState> BuildAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        CancellationToken ct = default)
    {
        var tiles = await ResolveTilesAsync(session.TenantId, session.PlanTierSnapshot, ct);
        var greetingAr = BuildGreeting(profile.DisplayName, "ar");
        var greetingEn = BuildGreeting(profile.DisplayName, "en");
        var resumeTarget = await FindResumeTargetAsync(session.StudentProfileId, session.Id, ct);
        var recommended = await RecommendTopicsAsync(profile, ct);

        return new HomeDashboardState(
            SessionId: session.Id,
            CorrelationId: session.CorrelationId,
            TenantId: session.TenantId,
            TutorLanguage: session.TutorLanguage,
            CurriculumType: profile.CurriculumType,
            Grade: profile.Grade,
            PlanTierSnapshot: session.PlanTierSnapshot,
            DeviceClass: session.DeviceClass,
            ModeTileStates: tiles,
            ResumeTarget: resumeTarget,
            RecommendedTopics: recommended,
            GreetingTextAr: greetingAr,
            GreetingTextEn: greetingEn,
            RenderedAt: DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<ModeTileState>> ResolveTilesAsync(
        Guid tenantId,
        string planTier,
        CancellationToken ct = default)
    {
        var results = new List<ModeTileState>(OfferedModes.Length);
        foreach (var mode in OfferedModes)
        {
            var decision = await _planGate.EvaluateAsync(
                new PlanGateContext(mode, tenantId, planTier, SubjectId: null, Grade: null), ct);
            var planGate = decision.Allowed ? "open" : "closed";
            var subjectGate = IsSubjectGated(mode) ? "na" : "na";
            results.Add(new ModeTileState(
                Mode: mode,
                Enabled: decision.Allowed,
                Reason: decision.Reason,
                PlanGate: planGate,
                SubjectGate: subjectGate));
        }
        return results;
    }

    private static bool IsSubjectGated(string mode) =>
        // Whiteboard is the only subject-gated mode at MVP; the gate is
        // re-checked when the student actually enters a subject, so the
        // top-level tile returns "na" (not applicable at dashboard level).
        mode == StudentModes.Whiteboard;

    private static string BuildGreeting(string displayName, string locale)
    {
        return locale switch
        {
            "ar" => $"مرحبًا بعودتك يا {displayName}",
            _     => $"Welcome back, {displayName}",
        };
    }

    private async Task<ResumeTarget?> FindResumeTargetAsync(
        Guid studentProfileId, Guid currentSessionId, CancellationToken ct)
    {
        var previous = await _db.StudentSessions
            .AsNoTracking()
            .Where(s => s.StudentProfileId == studentProfileId && s.Id != currentSessionId)
            .Where(s => s.ActiveMode != StudentModes.Home)
            .OrderByDescending(s => s.SessionLastActivityAt)
            .FirstOrDefaultAsync(ct);
        if (previous is null) return null;

        var deepLink = previous.ActiveMode switch
        {
            StudentModes.Study when previous.ActiveLessonId is { } lid => $"/study/lesson/{lid}",
            StudentModes.Study when previous.ActiveSubjectId is { } sid => $"/study/{sid}",
            StudentModes.TutorChat => "/tutor/chat",
            StudentModes.TutorVoice => "/tutor/voice",
            StudentModes.SolveQuestions => "/solve-questions",
            StudentModes.MockTest => "/mock-test",
            StudentModes.HomeworkHelp => "/homework-help",
            StudentModes.Whiteboard => "/whiteboard",
            _ => "/home",
        };
        return new ResumeTarget(previous.ActiveMode, deepLink);
    }

    private async Task<IReadOnlyList<RecommendedTopic>> RecommendTopicsAsync(
        StudentProfile profile, CancellationToken ct)
    {
        // Phase 3 MVP: recommendations come from the student's enrolled
        // subjects as a simple surface. Real recommendation pipeline is a
        // Phase 4 concern (engagement + mastery). Keep this deterministic
        // and bounded so the home page first-frame budget holds.
        var enrolled = ParseArray(profile.SubjectsEnrolled);
        return enrolled
            .Take(3)
            .Select(raw => new RecommendedTopic(
                TopicId: Guid.Empty,
                SubjectId: Guid.TryParse(raw, out var sid) ? sid : Guid.Empty,
                DisplayNameAr: raw,
                DisplayNameEn: raw))
            .ToList();
    }

    private static List<string> ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }
}

public static class HomeDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3HomeDashboard(this IServiceCollection services)
    {
        services.AddScoped<IHomeDashboardService, HomeDashboardService>();
        return services;
    }
}
