using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Notifications.DeliveryTracking;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Notifications.RetryAndDeadLetter;

/// <summary>
/// T059 (US2) — Retry + dead-letter processor for notification delivery.
///
/// Retry intervals follow the provider contract: 30s, 5m, 30m. After 3 failed
/// attempts the receipt becomes <c>dead_lettered</c>, a Phase 6 operational
/// event (<c>notification_dead_lettered</c>) is emitted, and an in-app
/// operator alert is dispatched through the existing
/// <see cref="INotificationChannelAdapterRegistry"/>.
/// </summary>
public interface INotificationRetryService
{
    Task<int> RunOnceAsync(CancellationToken ct = default);
    IReadOnlyList<TimeSpan> RetrySchedule { get; }
}

public sealed class NotificationRetryService : INotificationRetryService
{
    public static readonly IReadOnlyList<TimeSpan> RetryIntervals = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
    };
    public const int MaxRetries = 3;

    private readonly MuallimiDbContext _db;
    private readonly INotificationDeliveryTracker _tracker;
    private readonly Phase6OperationalEventOutbox _outbox;
    private readonly INotificationChannelAdapterRegistry _channels;
    private readonly ILogger<NotificationRetryService> _logger;

    public NotificationRetryService(
        MuallimiDbContext db,
        INotificationDeliveryTracker tracker,
        Phase6OperationalEventOutbox outbox,
        INotificationChannelAdapterRegistry channels,
        ILogger<NotificationRetryService> logger)
    {
        _db = db;
        _tracker = tracker;
        _outbox = outbox;
        _channels = channels;
        _logger = logger;
    }

    public IReadOnlyList<TimeSpan> RetrySchedule => RetryIntervals;

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var due = await _db.NotificationDeliveryReceipts
            .Where(r => r.Status == "failed" && r.NextRetryAt != null && r.NextRetryAt <= now)
            .Take(200)
            .ToListAsync(ct);

        var processed = 0;
        foreach (var receipt in due)
        {
            ct.ThrowIfCancellationRequested();
            if (receipt.RetryCount >= MaxRetries)
            {
                await DeadLetterAsync(receipt, ct);
                processed++;
                continue;
            }

            var updated = await _tracker.RedispatchAsync(receipt.ReceiptId, ct);
            if (updated is null) continue;

            if (updated.Status == "failed")
            {
                // Schedule the next retry, or dead-letter if exhausted.
                if (updated.RetryCount >= MaxRetries)
                {
                    await DeadLetterAsync(updated, ct);
                }
                else
                {
                    var delay = RetryIntervals[Math.Min(updated.RetryCount, RetryIntervals.Count - 1)];
                    updated.NextRetryAt = DateTime.UtcNow.Add(delay);
                    await _db.SaveChangesAsync(ct);
                }
            }
            else
            {
                updated.NextRetryAt = null;
                await _db.SaveChangesAsync(ct);
            }
            processed++;
        }
        return processed;
    }

    /// <summary>
    /// Called by <see cref="NotificationDeliveryTracker"/> callers right after a
    /// dispatch attempt returns <c>status = "failed"</c>, to enqueue the next
    /// retry. Keeps the schedule math in one place.
    /// </summary>
    public static DateTime? ResolveNextRetryAt(int retryCount, DateTime now)
    {
        if (retryCount >= MaxRetries) return null;
        var delay = RetryIntervals[Math.Min(retryCount, RetryIntervals.Count - 1)];
        return now.Add(delay);
    }

    private async Task DeadLetterAsync(NotificationDeliveryReceipt receipt, CancellationToken ct)
    {
        receipt.Status = "dead_lettered";
        receipt.NextRetryAt = null;
        await _db.SaveChangesAsync(ct);

        await _outbox.EnqueueAsync(
            receipt.TenantId,
            "notification_dead_lettered",
            new
            {
                receipt_id = receipt.ReceiptId,
                notification_id = receipt.NotificationId,
                channel = receipt.Channel,
                provider_name = receipt.ProviderName,
                retry_count = receipt.RetryCount,
                failure_reason = receipt.FailureReason,
            },
            receipt.CorrelationId,
            ct);

        try
        {
            var alert = _channels.Get("in_app");
            await alert.DispatchAsync(new NotificationDispatchRequest(
                TenantId: receipt.TenantId,
                RecipientUserId: receipt.RecipientId,
                RecipientEmail: null,
                NotificationKind: "operator.notification_dead_lettered",
                Language: "ar",
                Title: "notification.dead_letter",
                Body: $"channel={receipt.Channel} reason={receipt.FailureReason}",
                Metadata: new Dictionary<string, string> { ["receipt_id"] = receipt.ReceiptId.ToString("D") },
                CorrelationId: receipt.CorrelationId), ct);
        }
        catch (Exception ex)
        {
            // Operator alert is best-effort — the dead-letter row is durable.
            _logger.LogWarning(ex,
                "Dead-letter operator alert failed for receipt {ReceiptId}", receipt.ReceiptId);
        }
    }
}

public sealed class NotificationRetryHostedServiceOptions
{
    public bool EnableBackgroundLoop { get; set; } = false;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class NotificationRetryHostedService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NotificationRetryHostedService> _logger;
    private readonly NotificationRetryHostedServiceOptions _options;

    public NotificationRetryHostedService(
        IServiceProvider services,
        ILogger<NotificationRetryHostedService> logger,
        Microsoft.Extensions.Options.IOptions<NotificationRetryHostedServiceOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundLoop) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<INotificationRetryService>();
                await svc.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NotificationRetryHostedService tick failed");
            }
            await Task.Delay(_options.Interval, stoppingToken);
        }
    }
}

public static class NotificationRetryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase6NotificationRetryService(this IServiceCollection services)
    {
        services.AddScoped<INotificationRetryService, NotificationRetryService>();
        services.AddHostedService<NotificationRetryHostedService>();
        return services;
    }
}
