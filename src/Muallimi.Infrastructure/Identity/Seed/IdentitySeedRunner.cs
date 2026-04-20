using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Muallimi.Infrastructure.Identity.Seed;

/// <summary>
/// T047 — Orchestrates the Phase 9 identity seeds. Runs the tenant
/// seeder first (so UserRole row FKs can reference the Platform tenant),
/// then the role seeder. Idempotent — safe to call on every startup.
/// SuperAdmin bootstrap is deliberately NOT part of this runner; it
/// lands in US3 (T067 / <c>SuperAdminSeeder</c>) after the password
/// service is DI-registered.
/// </summary>
public sealed class IdentitySeedRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger<IdentitySeedRunner> _logger;

    public IdentitySeedRunner(IServiceProvider services, ILogger<IdentitySeedRunner> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        var tenantSeeder = sp.GetRequiredService<TenantSeeder>();
        var tenantCreated = await tenantSeeder.EnsureSeededAsync(ct).ConfigureAwait(false);
        if (tenantCreated)
        {
            _logger.LogInformation("Identity seed: Platform tenant created.");
        }

        var roleSeeder = sp.GetRequiredService<RoleSeeder>();
        var rolesInserted = await roleSeeder.EnsureSeededAsync(ct).ConfigureAwait(false);
        if (rolesInserted > 0)
        {
            _logger.LogInformation("Identity seed: {Count} role(s) inserted.", rolesInserted);
        }

        // T106: super-admin bootstrap — runs after tenant + roles so the
        // Platform tenant and super-admin role both exist. Idempotent by
        // email, no-op if SUPER_ADMIN_EMAIL / SUPER_ADMIN_INITIAL_PASSWORD
        // are unset.
        var superAdmin = sp.GetRequiredService<SuperAdminSeeder>();
        var outcome = await superAdmin.EnsureSeededAsync(ct).ConfigureAwait(false);
        if (outcome == SuperAdminSeedOutcome.Seeded)
        {
            _logger.LogInformation("Identity seed: super-admin seeded from env.");
        }
    }
}
