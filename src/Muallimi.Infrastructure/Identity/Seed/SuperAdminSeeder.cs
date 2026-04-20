using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Identity.Seed;

/// <summary>
/// T105 — Idempotent super-admin bootstrap. Reads three env vars:
///   • <c>SUPER_ADMIN_EMAIL</c>
///   • <c>SUPER_ADMIN_INITIAL_PASSWORD</c>
///   • <c>SUPER_ADMIN_ROLES</c> (comma-separated platform roles — first
///     one is always <c>super-admin</c> even if absent from the list)
///
/// The seeded user lives on the Platform tenant, has
/// <c>RequiresPasswordReset=true</c> so first login forces rotation,
/// and has <c>TwoFactorEnabled=false</c> with the expectation that
/// TOTP enrollment happens immediately after forced rotation (US4
/// lands the enrollment UI — until then the gate is flagged in audit).
/// Idempotent by normalized email: running a second time with the
/// same email is a no-op, running with a different email leaves the
/// existing super-admin untouched and skips.
/// </summary>
public sealed class SuperAdminSeeder
{
    public const string EmailEnvKey = "SUPER_ADMIN_EMAIL";
    public const string PasswordEnvKey = "SUPER_ADMIN_INITIAL_PASSWORD";
    public const string RolesEnvKey = "SUPER_ADMIN_ROLES";

    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly IConfiguration _config;
    private readonly ILogger<SuperAdminSeeder> _logger;

    public SuperAdminSeeder(
        MuallimiDbContext db,
        IPasswordService passwords,
        IConfiguration config,
        ILogger<SuperAdminSeeder> logger)
    {
        _db = db;
        _passwords = passwords;
        _config = config;
        _logger = logger;
    }

    public Task<SuperAdminSeedOutcome> EnsureSeededAsync(CancellationToken ct = default)
        => EnsureSeededAsync(ReadConfig(), ct);

    public async Task<SuperAdminSeedOutcome> EnsureSeededAsync(
        SuperAdminSeedConfig cfg,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.Email) || string.IsNullOrWhiteSpace(cfg.InitialPassword))
        {
            return SuperAdminSeedOutcome.Skipped;
        }

        var normalized = cfg.Email.Trim().ToLowerInvariant();
        var existingSuperAdminCount = await _db.IdentityUsers
            .IgnoreQueryFilters()
            .Join(_db.IdentityUserRoles.IgnoreQueryFilters(),
                u => u.Id, ur => ur.UserId, (u, ur) => new { u, ur })
            .Join(_db.IdentityRoles.IgnoreQueryFilters(),
                x => x.ur.RoleId, r => r.Id, (x, r) => new { x.u, x.ur, r })
            .Where(x => x.r.Name == "super-admin" && x.ur.RevokedAt == null && x.u.Status != UserStatus.Archived)
            .CountAsync(ct).ConfigureAwait(false);

        if (existingSuperAdminCount > 0)
        {
            return SuperAdminSeedOutcome.AlreadySeeded;
        }

        var existingByEmail = await _db.IdentityUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        if (existingByEmail is not null)
        {
            return SuperAdminSeedOutcome.AlreadySeeded;
        }

        var platform = await _db.IdentityTenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Type == TenantType.Platform, ct).ConfigureAwait(false);
        if (platform is null)
        {
            return SuperAdminSeedOutcome.Skipped;
        }

        var roleNames = new List<string> { "super-admin" };
        foreach (var r in cfg.AdditionalRoles)
        {
            if (!string.IsNullOrWhiteSpace(r) && !roleNames.Contains(r, StringComparer.OrdinalIgnoreCase))
            {
                roleNames.Add(r.Trim().ToLowerInvariant());
            }
        }

        var roles = await _db.IdentityRoles
            .IgnoreQueryFilters()
            .Where(r => roleNames.Contains(r.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        var platformRoles = roles
            .Where(r => r.Scope == RoleScope.Platform)
            .ToList();
        if (!platformRoles.Any(r => r.Name == "super-admin"))
        {
            return SuperAdminSeedOutcome.Skipped;
        }

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            TenantId = platform.Id,
            AccountType = AccountType.Personal,
            Email = cfg.Email.Trim(),
            NormalizedEmail = normalized,
            EmailVerified = true,
            EmailVerifiedAt = DateTime.UtcNow,
            FullName = string.IsNullOrWhiteSpace(cfg.FullName) ? "Super Admin" : cfg.FullName.Trim(),
            Locale = string.IsNullOrWhiteSpace(cfg.Locale) ? "ar" : cfg.Locale,
            Status = UserStatus.Active,
            PasswordHash = _passwords.Hash(cfg.InitialPassword),
            PasswordChangedAt = DateTime.UtcNow,
            RequiresPasswordReset = true,
            TwoFactorEnabled = false,
            CreatedAt = DateTime.UtcNow,
        };
        user.AssertAccountTypeInvariants();

        _db.IdentityUsers.Add(user);
        foreach (var role in platformRoles)
        {
            _db.IdentityUserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = role.Id,
                TenantId = platform.Id,
                GrantedBy = userId,
                GrantedAt = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Super-admin seeded email={Email} roles={Roles} requires_password_reset=true",
            cfg.Email, string.Join(",", platformRoles.Select(r => r.Name)));

        return SuperAdminSeedOutcome.Seeded;
    }

    public SuperAdminSeedConfig ReadConfig()
    {
        var email = _config[EmailEnvKey] ?? Environment.GetEnvironmentVariable(EmailEnvKey);
        var password = _config[PasswordEnvKey] ?? Environment.GetEnvironmentVariable(PasswordEnvKey);
        var rolesRaw = _config[RolesEnvKey] ?? Environment.GetEnvironmentVariable(RolesEnvKey);
        var extras = string.IsNullOrWhiteSpace(rolesRaw)
            ? Array.Empty<string>()
            : rolesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new SuperAdminSeedConfig(
            Email: email,
            InitialPassword: password,
            AdditionalRoles: extras,
            FullName: _config["SUPER_ADMIN_FULL_NAME"]
                ?? Environment.GetEnvironmentVariable("SUPER_ADMIN_FULL_NAME"),
            Locale: _config["SUPER_ADMIN_LOCALE"]
                ?? Environment.GetEnvironmentVariable("SUPER_ADMIN_LOCALE"));
    }
}

public sealed record SuperAdminSeedConfig(
    string? Email,
    string? InitialPassword,
    IReadOnlyList<string> AdditionalRoles,
    string? FullName = null,
    string? Locale = null);

public enum SuperAdminSeedOutcome
{
    Skipped = 0,
    Seeded = 1,
    AlreadySeeded = 2,
}
