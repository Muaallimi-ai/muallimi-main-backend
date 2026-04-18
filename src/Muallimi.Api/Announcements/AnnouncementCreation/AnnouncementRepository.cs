using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Announcements.AnnouncementCreation;

/// <summary>
/// T157 (US8) — repositories for the <see cref="Announcement"/> aggregate
/// and its per-recipient <see cref="AnnouncementDelivery"/> rows. Every
/// read path is school-tenant scoped so announcements cannot leak across
/// schools; writers defer <c>SaveChangesAsync</c> to the caller so the
/// Phase 5 outbox row is flushed in the same transaction.
/// </summary>
public interface IAnnouncementRepository
{
    Task AddAsync(Announcement row, CancellationToken ct = default);

    Task<Announcement?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid announcementId, CancellationToken ct = default);

    Task<IReadOnlyList<Announcement>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<IReadOnlyList<Announcement>> ListScheduledDueAsync(DateTime asOfUtc, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class AnnouncementRepository : IAnnouncementRepository
{
    private readonly MuallimiDbContext _db;

    public AnnouncementRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(Announcement row, CancellationToken ct = default)
    {
        if (row.AnnouncementId == Guid.Empty)
        {
            row.AnnouncementId = Guid.NewGuid();
        }
        if (row.CreatedAt == default)
        {
            row.CreatedAt = DateTime.UtcNow;
        }
        _db.Announcements.Add(row);
        return Task.CompletedTask;
    }

    public Task<Announcement?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid announcementId, CancellationToken ct = default)
        => _db.Announcements
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a =>
                a.TenantId == tenantId
                && a.SchoolTenantId == schoolTenantId
                && a.AnnouncementId == announcementId, ct);

    public async Task<IReadOnlyList<Announcement>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => await _db.Announcements
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.SchoolTenantId == schoolTenantId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Announcement>> ListScheduledDueAsync(DateTime asOfUtc, CancellationToken ct = default)
        => await _db.Announcements
            .IgnoreQueryFilters()
            .Where(a => a.Status == "scheduled"
                && a.ScheduledPublishAt != null
                && a.ScheduledPublishAt <= asOfUtc)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public interface IAnnouncementDeliveryRepository
{
    Task AddRangeAsync(IEnumerable<AnnouncementDelivery> rows, CancellationToken ct = default);

    Task<IReadOnlyList<AnnouncementDelivery>> ListForAnnouncementAsync(Guid tenantId, Guid announcementId, CancellationToken ct = default);

    Task<IReadOnlyList<AnnouncementDelivery>> ListForRecipientAsync(Guid tenantId, Guid recipientId, string recipientRole, CancellationToken ct = default);

    Task<int> CountByStatusAsync(Guid tenantId, Guid announcementId, string deliveryStatus, CancellationToken ct = default);

    Task MarkReadAsync(Guid tenantId, Guid announcementDeliveryId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class AnnouncementDeliveryRepository : IAnnouncementDeliveryRepository
{
    private readonly MuallimiDbContext _db;

    public AnnouncementDeliveryRepository(MuallimiDbContext db) => _db = db;

    public Task AddRangeAsync(IEnumerable<AnnouncementDelivery> rows, CancellationToken ct = default)
    {
        foreach (var row in rows)
        {
            if (row.AnnouncementDeliveryId == Guid.Empty)
            {
                row.AnnouncementDeliveryId = Guid.NewGuid();
            }
            _db.AnnouncementDeliveries.Add(row);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AnnouncementDelivery>> ListForAnnouncementAsync(Guid tenantId, Guid announcementId, CancellationToken ct = default)
        => await _db.AnnouncementDeliveries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.AnnouncementId == announcementId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AnnouncementDelivery>> ListForRecipientAsync(Guid tenantId, Guid recipientId, string recipientRole, CancellationToken ct = default)
        => await _db.AnnouncementDeliveries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                && d.RecipientId == recipientId
                && d.RecipientRole == recipientRole)
            .ToListAsync(ct);

    public Task<int> CountByStatusAsync(Guid tenantId, Guid announcementId, string deliveryStatus, CancellationToken ct = default)
        => _db.AnnouncementDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId
                && d.AnnouncementId == announcementId
                && d.DeliveryStatus == deliveryStatus)
            .CountAsync(ct);

    public async Task MarkReadAsync(Guid tenantId, Guid announcementDeliveryId, CancellationToken ct = default)
    {
        var row = await _db.AnnouncementDeliveries
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.AnnouncementDeliveryId == announcementDeliveryId, ct);
        if (row is null) return;
        row.DeliveryStatus = "read";
        row.ReadAt = DateTime.UtcNow;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class AnnouncementRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5AnnouncementRepository(this IServiceCollection services)
    {
        services.AddScoped<IAnnouncementRepository, AnnouncementRepository>();
        services.AddScoped<IAnnouncementDeliveryRepository, AnnouncementDeliveryRepository>();
        return services;
    }
}
