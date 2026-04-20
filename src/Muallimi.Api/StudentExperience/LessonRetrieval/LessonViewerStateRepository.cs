using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.LessonRetrieval;

/// <summary>
/// T049 (US2) — LessonViewerStateRepository.
///
/// Persists per-session lesson viewer state: playback position, rate,
/// captions toggle, and the bound Phase 1 teacher voice profile. The row
/// is tenant-scoped via <see cref="ITenantScoped"/> so the EF Core global
/// filter scopes every read. The repository is idempotent on
/// (student_session_id, lesson_id) — resuming a lesson updates the same
/// row rather than creating a new one.
/// </summary>
public interface ILessonViewerStateRepository
{
    Task<LessonViewerState?> FindAsync(
        Guid studentSessionId, Guid lessonId, CancellationToken ct = default);

    Task<LessonViewerState> UpsertAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid lessonId,
        string teacherVoiceProfileId,
        LessonViewerPositionInput position,
        CancellationToken ct = default);
}

public sealed record LessonViewerPositionInput(
    double PositionSeconds,
    double Rate,
    bool CaptionsEnabled,
    string PlaybackState);

public sealed class LessonViewerStateRepository : ILessonViewerStateRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public LessonViewerStateRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public Task<LessonViewerState?> FindAsync(
        Guid studentSessionId, Guid lessonId, CancellationToken ct = default)
    {
        return _db.LessonViewerStates
            .FirstOrDefaultAsync(
                s => s.StudentSessionId == studentSessionId && s.LessonId == lessonId, ct);
    }

    public async Task<LessonViewerState> UpsertAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid lessonId,
        string teacherVoiceProfileId,
        LessonViewerPositionInput position,
        CancellationToken ct = default)
    {
        var row = await _db.LessonViewerStates
            .FirstOrDefaultAsync(
                s => s.StudentSessionId == studentSessionId && s.LessonId == lessonId, ct);

        var now = DateTime.UtcNow;
        var positionJson = JsonSerializer.Serialize(
            new { position_seconds = position.PositionSeconds },
            JsonOptions);

        if (row is null)
        {
            row = new LessonViewerState
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StudentSessionId = studentSessionId,
                LessonId = lessonId,
                ViewerPosition = positionJson,
                PlaybackState = NormalisePlayback(position.PlaybackState),
                TeacherVoiceProfileId = teacherVoiceProfileId,
                CaptionsEnabled = position.CaptionsEnabled,
                Rate = position.Rate <= 0 ? 1.0 : position.Rate,
                LastInteractionAt = now,
            };
            _db.LessonViewerStates.Add(row);
        }
        else
        {
            row.ViewerPosition = positionJson;
            row.PlaybackState = NormalisePlayback(position.PlaybackState);
            row.CaptionsEnabled = position.CaptionsEnabled;
            row.Rate = position.Rate <= 0 ? row.Rate : position.Rate;
            row.TeacherVoiceProfileId = teacherVoiceProfileId;
            row.LastInteractionAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static string NormalisePlayback(string? state) => state switch
    {
        "playing" or "paused" or "ended" or "idle" => state,
        _                                           => "idle",
    };
}

public static class LessonViewerStateRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3LessonViewerState(this IServiceCollection services)
    {
        services.AddScoped<ILessonViewerStateRepository, LessonViewerStateRepository>();
        return services;
    }
}
