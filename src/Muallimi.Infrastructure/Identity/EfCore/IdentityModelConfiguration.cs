using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Identity.EfCore;

/// <summary>
/// T024/T025 — Phase 9 Identity EF configuration + tenant query filter.
/// Keeps the per-module configure-in-one-place convention used by Phases
/// 1-6 (see <see cref="MuallimiDbContext.OnModelCreating"/>). Tenant-scoped
/// entities implementing <see cref="ITenantScoped"/> receive the same
/// global filter the rest of the platform uses.
/// </summary>
public static class IdentityModelConfiguration
{
    public static void ConfigurePhase9Identity(this ModelBuilder modelBuilder)
    {
        // ── Tenant ────────────────────────────────────────────────
        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("identity_tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Type).HasColumnName("type").HasConversion<int>();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Locale).HasColumnName("locale").HasMaxLength(5).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            e.HasIndex(x => new { x.Type, x.DisplayName });
            e.HasIndex(x => x.Status);
        });

        // ── User ──────────────────────────────────────────────────
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("identity_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AccountType).HasColumnName("account_type").HasConversion<int>();
            e.Property(x => x.ManagedByUserId).HasColumnName("managed_by_user_id");
            e.Property(x => x.Username).HasColumnName("username").HasMaxLength(50);
            e.Property(x => x.NormalizedUsername).HasColumnName("normalized_username").HasMaxLength(50);
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(255);
            e.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(255);
            e.Property(x => x.EmailVerified).HasColumnName("email_verified");
            e.Property(x => x.EmailVerifiedAt).HasColumnName("email_verified_at");
            e.Property(x => x.PhoneNumber).HasColumnName("phone_number").HasMaxLength(20);
            e.Property(x => x.PhoneVerified).HasColumnName("phone_verified");
            e.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(100).IsRequired();
            e.Property(x => x.FullNameEn).HasColumnName("full_name_en").HasMaxLength(100);
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(500);
            e.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at");
            e.Property(x => x.RequiresPasswordReset).HasColumnName("requires_password_reset");
            e.Property(x => x.TwoFactorEnabled).HasColumnName("two_factor_enabled");
            e.Property(x => x.Locale).HasColumnName("locale").HasMaxLength(5);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            e.Property(x => x.LockoutEnd).HasColumnName("lockout_end");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            e.Property(x => x.LastLoginIp).HasColumnName("last_login_ip").HasMaxLength(45);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            // Globally unique on normalized email where present
            e.HasIndex(x => x.NormalizedEmail)
                .IsUnique()
                .HasFilter("normalized_email IS NOT NULL")
                .HasDatabaseName("ix_identity_users_normalized_email_unique");
            // Globally unique on normalized username where present
            e.HasIndex(x => x.NormalizedUsername)
                .IsUnique()
                .HasFilter("normalized_username IS NOT NULL")
                .HasDatabaseName("ix_identity_users_normalized_username_unique");
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.ManagedByUserId);
            e.HasIndex(x => x.Status);
        });

        // ── Role ──────────────────────────────────────────────────
        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("identity_roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.Scope).HasColumnName("scope").HasConversion<int>();
            e.Property(x => x.IsSystem).HasColumnName("is_system");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ix_identity_roles_name_unique");
        });

        // ── UserRole ──────────────────────────────────────────────
        modelBuilder.Entity<UserRole>(e =>
        {
            e.ToTable("identity_user_roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.GrantedBy).HasColumnName("granted_by");
            e.Property(x => x.GrantedAt).HasColumnName("granted_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            // Unique active grant per (user, role, tenant)
            e.HasIndex(x => new { x.UserId, x.RoleId, x.TenantId })
                .IsUnique()
                .HasFilter("revoked_at IS NULL")
                .HasDatabaseName("ix_identity_user_roles_active_unique");
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.RoleId);
        });

        // ── RefreshToken ──────────────────────────────────────────
        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("identity_refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(100);
            e.Property(x => x.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
            e.Property(x => x.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(45);
            e.HasIndex(x => x.TokenHash).HasDatabaseName("ix_identity_refresh_tokens_token_hash");
            e.HasIndex(x => new { x.SessionId, x.RevokedAt });
            e.HasIndex(x => x.UserId);
        });

        // ── UserSession ───────────────────────────────────────────
        modelBuilder.Entity<UserSession>(e =>
        {
            e.ToTable("identity_user_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.DeviceName).HasColumnName("device_name").HasMaxLength(200);
            e.Property(x => x.DeviceType).HasColumnName("device_type").HasConversion<int>();
            e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
            e.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
        });

        // ── LoginAttempt ──────────────────────────────────────────
        modelBuilder.Entity<LoginAttempt>(e =>
        {
            e.ToTable("identity_login_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(255);
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
            e.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(500);
            e.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<int>();
            e.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(200);
            e.Property(x => x.AttemptedAt).HasColumnName("attempted_at");
            e.HasIndex(x => new { x.Email, x.AttemptedAt });
            e.HasIndex(x => new { x.IpAddress, x.AttemptedAt });
        });

        // ── EmailVerificationToken ────────────────────────────────
        modelBuilder.Entity<EmailVerificationToken>(e =>
        {
            e.ToTable("identity_email_verification_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.HasIndex(x => x.TokenHash).HasDatabaseName("ix_identity_email_verification_tokens_token_hash");
            e.HasIndex(x => new { x.UserId, x.UsedAt });
        });

        // ── PasswordResetToken ────────────────────────────────────
        modelBuilder.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("identity_password_reset_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
            e.HasIndex(x => x.TokenHash).HasDatabaseName("ix_identity_password_reset_tokens_token_hash");
            e.HasIndex(x => new { x.UserId, x.UsedAt });
        });

        // ── TwoFactorSecret ───────────────────────────────────────
        modelBuilder.Entity<TwoFactorSecret>(e =>
        {
            e.ToTable("identity_two_factor_secrets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Secret).HasColumnName("secret").HasMaxLength(500).IsRequired();
            e.Property(x => x.RecoveryCodes).HasColumnName("recovery_codes").HasMaxLength(2000).IsRequired();
            e.Property(x => x.EnabledAt).HasColumnName("enabled_at");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            e.HasIndex(x => x.UserId).IsUnique().HasDatabaseName("ix_identity_two_factor_secrets_user_unique");
        });

        // ── ImpersonationSession ──────────────────────────────────
        modelBuilder.Entity<ImpersonationSession>(e =>
        {
            e.ToTable("identity_impersonation_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ImpersonatorId).HasColumnName("impersonator_id");
            e.Property(x => x.TargetUserId).HasColumnName("target_user_id");
            e.Property(x => x.TargetTenantId).HasColumnName("target_tenant_id");
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(50);
            e.HasIndex(x => x.ImpersonatorId);
            e.HasIndex(x => x.TargetUserId);
        });

        // ── BackfillError ─────────────────────────────────────────
        // T167: records conflicts/failures during the legacy AuthAPI backfill.
        modelBuilder.Entity<BackfillError>(e =>
        {
            e.ToTable("identity_backfill_errors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.LegacyUserId).HasColumnName("legacy_user_id");
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500).IsRequired();
            e.Property(x => x.Detail).HasColumnName("detail").HasMaxLength(2000);
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => x.LegacyUserId);
        });
    }
}
