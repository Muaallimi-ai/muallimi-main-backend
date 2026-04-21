using System;
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
/// Dev-friendly curriculum-admin bootstrap. Reads three env vars:
///   • <c>CURRICULUM_ADMIN_EMAIL</c>
///   • <c>CURRICULUM_ADMIN_INITIAL_PASSWORD</c>
///   • <c>CURRICULUM_ADMIN_FULL_NAME</c> (optional)
///   • <c>CURRICULUM_ADMIN_LOCALE</c> (optional, defaults to "ar")
///
/// Unlike <see cref="SuperAdminSeeder"/> this seeder sets
/// <c>RequiresPasswordReset=false</c> so the operator can log in
/// directly and land on the real curriculum-admin home surface. The
/// seeded user lives on the Platform tenant and is granted the
/// <c>curriculum-admin</c> role only. Idempotent by normalized email.
/// </summary>
public sealed class CurriculumAdminSeeder
{
    public const string EmailEnvKey = "CURRICULUM_ADMIN_EMAIL";
    public const string PasswordEnvKey = "CURRICULUM_ADMIN_INITIAL_PASSWORD";
    public const string FullNameEnvKey = "CURRICULUM_ADMIN_FULL_NAME";
    public const string LocaleEnvKey = "CURRICULUM_ADMIN_LOCALE";

    private const string RoleName = "curriculum-admin";

    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly IConfiguration _config;
    private readonly ILogger<CurriculumAdminSeeder> _logger;

    public CurriculumAdminSeeder(
        MuallimiDbContext db,
        IPasswordService passwords,
        IConfiguration config,
        ILogger<CurriculumAdminSeeder> logger)
    {
        _db = db;
        _passwords = passwords;
        _config = config;
        _logger = logger;
    }

    public Task<CurriculumAdminSeedOutcome> EnsureSeededAsync(CancellationToken ct = default)
        => EnsureSeededAsync(ReadConfig(), ct);

    public async Task<CurriculumAdminSeedOutcome> EnsureSeededAsync(
        CurriculumAdminSeedConfig cfg,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.Email) || string.IsNullOrWhiteSpace(cfg.InitialPassword))
        {
            return CurriculumAdminSeedOutcome.Skipped;
        }

        var normalized = cfg.Email.Trim().ToLowerInvariant();

        var existingByEmail = await _db.IdentityUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        if (existingByEmail is not null)
        {
            return CurriculumAdminSeedOutcome.AlreadySeeded;
        }

        var platform = await _db.IdentityTenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Type == TenantType.Platform, ct).ConfigureAwait(false);
        if (platform is null)
        {
            return CurriculumAdminSeedOutcome.Skipped;
        }

        var role = await _db.IdentityRoles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == RoleName && r.Scope == RoleScope.Platform, ct)
            .ConfigureAwait(false);
        if (role is null)
        {
            return CurriculumAdminSeedOutcome.Skipped;
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
            FullName = string.IsNullOrWhiteSpace(cfg.FullName) ? "Curriculum Admin" : cfg.FullName.Trim(),
            Locale = string.IsNullOrWhiteSpace(cfg.Locale) ? "ar" : cfg.Locale,
            Status = UserStatus.Active,
            PasswordHash = _passwords.Hash(cfg.InitialPassword),
            PasswordChangedAt = DateTime.UtcNow,
            RequiresPasswordReset = false,
            TwoFactorEnabled = false,
            CreatedAt = DateTime.UtcNow,
        };
        user.AssertAccountTypeInvariants();

        _db.IdentityUsers.Add(user);
        _db.IdentityUserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            TenantId = platform.Id,
            GrantedBy = userId,
            GrantedAt = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Curriculum-admin seeded email={Email} requires_password_reset=false",
            cfg.Email);

        return CurriculumAdminSeedOutcome.Seeded;
    }

    public CurriculumAdminSeedConfig ReadConfig()
    {
        var email = _config[EmailEnvKey] ?? Environment.GetEnvironmentVariable(EmailEnvKey);
        var password = _config[PasswordEnvKey] ?? Environment.GetEnvironmentVariable(PasswordEnvKey);
        var fullName = _config[FullNameEnvKey] ?? Environment.GetEnvironmentVariable(FullNameEnvKey);
        var locale = _config[LocaleEnvKey] ?? Environment.GetEnvironmentVariable(LocaleEnvKey);
        return new CurriculumAdminSeedConfig(
            Email: email,
            InitialPassword: password,
            FullName: fullName,
            Locale: locale);
    }
}

public sealed record CurriculumAdminSeedConfig(
    string? Email,
    string? InitialPassword,
    string? FullName = null,
    string? Locale = null);

public enum CurriculumAdminSeedOutcome
{
    Skipped = 0,
    Seeded = 1,
    AlreadySeeded = 2,
}
