using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Identity.Seed;

/// <summary>
/// T046 — Seeds the singleton <c>Platform</c> tenant. Family and School
/// tenants are never seeded — they're created at runtime (Family on
/// parent self-registration, School on super-admin onboarding).
/// Idempotent: re-running a seeded DB is a no-op.
/// </summary>
public sealed class TenantSeeder
{
    public const string PlatformTenantDisplayName = "Muaallimi Platform";
    public static readonly Guid PlatformTenantId = new("00000000-0000-0000-0000-000000000001");

    private readonly MuallimiDbContext _db;

    public TenantSeeder(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<bool> EnsureSeededAsync(CancellationToken ct = default)
    {
        var exists = await _db.IdentityTenants
            .AnyAsync(t => t.Type == TenantType.Platform, ct)
            .ConfigureAwait(false);
        if (exists) return false;

        _db.IdentityTenants.Add(new Tenant
        {
            Id = PlatformTenantId,
            Type = TenantType.Platform,
            DisplayName = PlatformTenantDisplayName,
            Locale = "ar",
            Status = TenantStatus.Active,
            Metadata = "{}",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
