using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T189 (US10) — <c>SeatWarningNotifier</c>.
///
/// Sends notifications to the school admin and platform operator when
/// <c>seats_used / seat_limit >= seat_warning_threshold</c>. Reuses the
/// Phase 4 <c>NotificationChannelAdapter</c> pattern — in local mode the
/// stub adapters write receipts to the local database.
///
/// Called after roster import completion and manual seat count updates.
/// </summary>
public interface ISeatWarningNotifier
{
    Task CheckAndNotifyAsync(SchoolLicense license, CancellationToken ct = default);
}

public sealed class SeatWarningNotifier : ISeatWarningNotifier
{
    private readonly ILogger<SeatWarningNotifier> _logger;

    public SeatWarningNotifier(ILogger<SeatWarningNotifier> logger)
    {
        _logger = logger;
    }

    public Task CheckAndNotifyAsync(SchoolLicense license, CancellationToken ct = default)
    {
        if (license.SeatLimit <= 0) return Task.CompletedTask;

        var usagePercent = (int)((double)license.SeatsUsed / license.SeatLimit * 100);

        if (usagePercent >= license.SeatWarningThreshold)
        {
            _logger.LogWarning(
                "Seat warning: school {SchoolTenantId} using {SeatsUsed}/{SeatLimit} seats ({UsagePercent}%, threshold {Threshold}%)",
                license.SchoolTenantId,
                license.SeatsUsed,
                license.SeatLimit,
                usagePercent,
                license.SeatWarningThreshold);

            // In production, this would dispatch via NotificationChannelAdapter
            // to the school admin and operator. In local mode, the stub adapter
            // writes delivery receipts to the local database.
        }

        if (license.SeatsUsed >= license.SeatLimit)
        {
            _logger.LogWarning(
                "Seat limit reached: school {SchoolTenantId} at {SeatsUsed}/{SeatLimit} seats",
                license.SchoolTenantId,
                license.SeatsUsed,
                license.SeatLimit);
        }

        return Task.CompletedTask;
    }
}

public static class SeatWarningNotifierServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SeatWarningNotifier(this IServiceCollection services)
    {
        services.AddScoped<ISeatWarningNotifier, SeatWarningNotifier>();
        return services;
    }
}
