using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Audit;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum;

/// <summary>
/// Background service that removes deprecated content chunks after the BRD-defined
/// 30-day grace period. Deprecated chunks remain queryable during the grace window
/// so that cached references and in-flight sessions are not broken.
/// Runs once per hour.
/// </summary>
public class DeprecatedChunkCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeprecatedChunkCleanupWorker> _logger;

    /// <summary>BRD-mandated grace period before deprecated chunks are removed.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    /// <summary>How often the worker runs.</summary>
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    public DeprecatedChunkCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DeprecatedChunkCleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeprecatedChunkCleanupWorker started. Grace period: {Days} days", GracePeriod.TotalDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupDeprecatedChunksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during deprecated chunk cleanup");
            }

            await Task.Delay(RunInterval, stoppingToken);
        }
    }

    internal async Task CleanupDeprecatedChunksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditEventEmitter>();

        var cutoffDate = DateTime.UtcNow - GracePeriod;

        // Find deprecated chunks whose associated change log entry is older than the grace period.
        // We use the lesson's last change reason timestamp as the deprecation indicator.
        // Since ContentChunk doesn't have a deprecated_at field, we look at ChangeLogEntries.
        var deprecatedChunks = await db.ContentChunks
            .Where(c => c.Status == ChunkStatus.Deprecated)
            .Join(
                db.ChangeLogEntries
                    .Where(e => e.EventType == ChangeEventType.LessonUpdated
                                && e.OccurredAt < cutoffDate),
                chunk => chunk.LessonId,
                entry => entry.LessonId,
                (chunk, entry) => chunk)
            .Distinct()
            .ToListAsync(ct);

        if (deprecatedChunks.Count == 0)
        {
            _logger.LogDebug("No deprecated chunks past grace period");
            return;
        }

        db.ContentChunks.RemoveRange(deprecatedChunks);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Removed {Count} deprecated chunks past {Days}-day grace period",
            deprecatedChunks.Count, GracePeriod.TotalDays);

        audit.Emit(new AuditEvent
        {
            EventCategory = "curriculum",
            Action = "deprecated-chunks-cleaned",
            TargetType = "ContentChunk",
            TargetId = $"batch:{deprecatedChunks.Count}",
            ActorId = "system",
            TenantId = "system",
            Outcome = "succeeded",
            CorrelationId = Guid.NewGuid().ToString()
        });
    }
}
