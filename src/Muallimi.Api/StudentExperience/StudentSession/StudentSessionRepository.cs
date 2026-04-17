using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.StudentSession;

/// <summary>
/// T017 — StudentSessionRepository with a small mode transition state
/// machine. Transitions:
///   home → study | tutor_chat | tutor_voice | solve_questions | mock_test
///        | homework_help | whiteboard → home
/// <c>session_end</c> is terminal.
///
/// Plan-gate re-check (constitution rule) lives in the endpoint layer; this
/// repository only owns row lifecycle. Every write updates
/// <c>SessionLastActivityAt</c> so idle-timeout detection is possible.
/// </summary>
public static class StudentModes
{
    public const string Home = "home";
    public const string Study = "study";
    public const string TutorChat = "tutor_chat";
    public const string TutorVoice = "tutor_voice";
    public const string SolveQuestions = "solve_questions";
    public const string MockTest = "mock_test";
    public const string HomeworkHelp = "homework_help";
    public const string Whiteboard = "whiteboard";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Home, Study, TutorChat, TutorVoice, SolveQuestions,
        MockTest, HomeworkHelp, Whiteboard,
    };
}

public interface IStudentSessionRepository
{
    Task<Muallimi.Domain.StudentExperience.StudentSession> StartAsync(
        Guid tenantId,
        Guid studentProfileId,
        Guid correlationId,
        string tutorLanguage,
        string deviceClass,
        string planTierSnapshot,
        CancellationToken ct = default);

    Task<Muallimi.Domain.StudentExperience.StudentSession?> FindAsync(Guid sessionId, CancellationToken ct = default);

    Task<Muallimi.Domain.StudentExperience.StudentSession> TransitionModeAsync(
        Guid sessionId, string targetMode, CancellationToken ct = default);

    Task<Muallimi.Domain.StudentExperience.StudentSession> EndAsync(
        Guid sessionId, string endReason, CancellationToken ct = default);
}

public sealed class StudentSessionRepository : IStudentSessionRepository
{
    private readonly MuallimiDbContext _db;

    public StudentSessionRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<Muallimi.Domain.StudentExperience.StudentSession> StartAsync(
        Guid tenantId,
        Guid studentProfileId,
        Guid correlationId,
        string tutorLanguage,
        string deviceClass,
        string planTierSnapshot,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentProfileId = studentProfileId,
            CorrelationId = correlationId,
            ActiveMode = StudentModes.Home,
            TutorLanguage = tutorLanguage,
            DeviceClass = deviceClass,
            PlanTierSnapshot = planTierSnapshot,
            SessionStartedAt = now,
            SessionLastActivityAt = now,
        };
        _db.StudentSessions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<Muallimi.Domain.StudentExperience.StudentSession?> FindAsync(Guid sessionId, CancellationToken ct = default)
    {
        return await _db.StudentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
    }

    public async Task<Muallimi.Domain.StudentExperience.StudentSession> TransitionModeAsync(
        Guid sessionId, string targetMode, CancellationToken ct = default)
    {
        if (!StudentModes.All.Contains(targetMode))
        {
            throw new InvalidOperationException($"Unknown mode '{targetMode}'.");
        }

        var row = await _db.StudentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");
        if (row.SessionEndedAt is not null)
        {
            throw new InvalidOperationException("Cannot transition an ended session.");
        }
        if (!IsLegalTransition(row.ActiveMode, targetMode))
        {
            throw new InvalidOperationException(
                $"Illegal mode transition {row.ActiveMode} -> {targetMode}. Must return via 'home'.");
        }
        row.ActiveMode = targetMode;
        row.SessionLastActivityAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<Muallimi.Domain.StudentExperience.StudentSession> EndAsync(
        Guid sessionId, string endReason, CancellationToken ct = default)
    {
        var row = await _db.StudentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new InvalidOperationException($"Session {sessionId} not found.");
        row.SessionEndedAt = DateTime.UtcNow;
        row.SessionLastActivityAt = row.SessionEndedAt.Value;
        row.EndReason = endReason;
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public static bool IsLegalTransition(string from, string to)
    {
        // Allowed transitions:
        //   home ↔ any mode
        //   any mode → home (returning to dashboard)
        //   same-mode refresh is always legal.
        if (string.Equals(from, to, StringComparison.Ordinal)) return true;
        if (from == StudentModes.Home) return true;
        if (to == StudentModes.Home) return true;
        return false;
    }
}

public static class StudentSessionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3StudentSessionRepository(this IServiceCollection services)
    {
        services.AddScoped<IStudentSessionRepository, StudentSessionRepository>();
        return services;
    }
}
