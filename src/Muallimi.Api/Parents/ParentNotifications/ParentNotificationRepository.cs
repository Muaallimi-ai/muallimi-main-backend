using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentNotifications;

/// <summary>
/// T128 (US7) — <see cref="ParentNotification"/> repository.
///
/// Writes queued / deferred / dispatched rows and reads the inbox for a
/// specific parent. Every query pins <c>tenant_id</c> explicitly so a
/// misconfigured ambient tenant cannot silently surface another family's
/// notifications; cross-family visibility is additionally blocked at the
/// endpoint layer against the parent's active <see cref="ChildLink"/> set.
/// </summary>
public interface IParentNotificationRepository
{
    Task AddAsync(ParentNotification notification, CancellationToken ct = default);

    Task<ParentNotification?> GetAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid parentNotificationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<ParentNotification>> ListForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        IReadOnlyCollection<Guid> allowedChildIds,
        int limit,
        CancellationToken ct = default);

    Task<int> CountUnreadForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        IReadOnlyCollection<Guid> allowedChildIds,
        CancellationToken ct = default);

    Task<int> MarkAllAsReadForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        IReadOnlyCollection<Guid> allowedChildIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<ParentNotification>> ListDeferredReadyForDispatchAsync(
        DateTime now,
        CancellationToken ct = default);

    Task UpdateAsync(ParentNotification notification, CancellationToken ct = default);

    /// <summary>
    /// Phase 9 Phase 4 dedup helper: returns the most recent
    /// <see cref="ParentNotification"/> for the given (parent, child,
    /// kind) tuple created on or after <paramref name="sinceUtc"/>, or
    /// null if none. Used by <c>ChildCredentialNotifier</c> to collapse
    /// repeated credential events into a single inbox row per day per
    /// child instead of stacking N rows.
    /// </summary>
    Task<ParentNotification?> FindLatestByKindAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid childId,
        string notificationKind,
        DateTime sinceUtc,
        CancellationToken ct = default);
}

public sealed class ParentNotificationRepository : IParentNotificationRepository
{
    private readonly MuallimiDbContext _db;

    public ParentNotificationRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(ParentNotification notification, CancellationToken ct = default)
    {
        _db.ParentNotifications.Add(notification);
        return Task.CompletedTask;
    }

    public Task<ParentNotification?> GetAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid parentNotificationId,
        CancellationToken ct = default)
        => _db.ParentNotifications
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                n => n.TenantId == tenantId
                     && n.ParentProfileId == parentProfileId
                     && n.ParentNotificationId == parentNotificationId,
                ct);

    public async Task<IReadOnlyList<ParentNotification>> ListForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        IReadOnlyCollection<Guid> allowedChildIds,
        int limit,
        CancellationToken ct = default)
    {
        if (allowedChildIds.Count == 0) return Array.Empty<ParentNotification>();
        var childSet = allowedChildIds.ToHashSet();
        return await _db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.ParentProfileId == parentProfileId)
            .Where(n => childSet.Contains(n.ChildId))
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public Task<int> CountUnreadForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        IReadOnlyCollection<Guid> allowedChildIds,
        CancellationToken ct = default)
    {
        if (allowedChildIds.Count == 0) return Task.FromResult(0);
        var childSet = allowedChildIds.ToHashSet();
        return _db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.ParentProfileId == parentProfileId)
            .Where(n => childSet.Contains(n.ChildId))
            .Where(n => n.DeliveryState == "dispatched" || n.DeliveryState == "deferred")
            .CountAsync(ct);
    }

    public async Task<int> MarkAllAsReadForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        IReadOnlyCollection<Guid> allowedChildIds,
        CancellationToken ct = default)
    {
        if (allowedChildIds.Count == 0) return 0;
        var childSet = allowedChildIds.ToHashSet();
        var rows = await _db.ParentNotifications
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.ParentProfileId == parentProfileId)
            .Where(n => childSet.Contains(n.ChildId))
            .Where(n => n.DeliveryState == "dispatched" || n.DeliveryState == "deferred")
            .ToListAsync(ct);
        foreach (var row in rows) row.DeliveryState = "read";
        return rows.Count;
    }

    public async Task<IReadOnlyList<ParentNotification>> ListDeferredReadyForDispatchAsync(
        DateTime now,
        CancellationToken ct = default)
        => await _db.ParentNotifications
            .IgnoreQueryFilters()
            .Where(n => n.DeliveryState == "deferred"
                        && n.QuietHoursDeferredUntil != null
                        && n.QuietHoursDeferredUntil <= now)
            .OrderBy(n => n.QuietHoursDeferredUntil)
            .ToListAsync(ct);

    public Task UpdateAsync(ParentNotification notification, CancellationToken ct = default)
    {
        _db.ParentNotifications.Update(notification);
        return Task.CompletedTask;
    }

    public Task<ParentNotification?> FindLatestByKindAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid childId,
        string notificationKind,
        DateTime sinceUtc,
        CancellationToken ct = default)
        => _db.ParentNotifications
            .IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId
                     && n.ParentProfileId == parentProfileId
                     && n.ChildId == childId
                     && n.NotificationKind == notificationKind
                     && n.CreatedAt >= sinceUtc)
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefaultAsync(ct);
}

public static class ParentNotificationRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ParentNotificationRepository(this IServiceCollection services)
    {
        services.AddScoped<IParentNotificationRepository, ParentNotificationRepository>();
        return services;
    }
}
