using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Identity.Filters;
using Muallimi.Api.Identity.Middleware;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Notifications;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Queries;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Validators;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Infrastructure.Identity.Adapters;
using Muallimi.Infrastructure.Identity.Cryptography;
using Muallimi.Infrastructure.Identity.Seed;
using Muallimi.Infrastructure.Persistence;
using StackExchange.Redis;

namespace Muallimi.Api.Identity.Startup;

/// <summary>
/// T041 + T042 — Single entry point that registers every Phase 9
/// Identity moving part: services, adapters, middleware, filters, seeders,
/// notification facade, and the CORS policy named after the identity
/// module.
///
/// Call from <c>Program.cs</c> exactly once:
/// <code>builder.Services.AddIdentityModule(builder.Configuration);</code>
///
/// The Redis connection is optional: if <c>Redis:ConnectionString</c>
/// (or the <c>REDIS_CONNECTION_STRING</c> env var) is unset, Identity
/// falls back to in-memory adapters for local dev / tests. Production
/// deployments MUST set the connection string — see the phase-0 docker
/// compose stack for local parity.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    public const string IdentityCorsPolicy = "IdentityCors";

    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Options from env / config ────────────────────────────
        var jwtSecret = configuration["Identity:Jwt:SecretKey"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_JWT_SECRET_KEY")
            ?? "dev-only-please-rotate-minimum-32-chars-secret-key";
        var jwtIssuer = configuration["Identity:Jwt:Issuer"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_JWT_ISSUER")
            ?? "muallimi-main-backend";
        var jwtAudience = configuration["Identity:Jwt:Audience"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_JWT_AUDIENCE")
            ?? "muallimi-platform";
        var accessMinutes = int.TryParse(
                configuration["Identity:Jwt:AccessTokenMinutes"]
                ?? Environment.GetEnvironmentVariable("IDENTITY_ACCESS_TOKEN_TTL_MINUTES"),
                out var am)
            ? am : 15;
        var refreshDays = int.TryParse(
                configuration["Identity:Jwt:RefreshTokenDays"]
                ?? Environment.GetEnvironmentVariable("IDENTITY_REFRESH_TOKEN_TTL_DAYS"),
                out var rd)
            ? rd : 7;

        services.AddSingleton(new JwtTokenServiceOptions
        {
            SecretKey = jwtSecret,
            Issuer = jwtIssuer,
            Audience = jwtAudience,
            AccessTokenMinutes = accessMinutes,
            RefreshTokenDays = refreshDays,
        });

        // ── Cryptography (AES-256-GCM for TOTP at rest) ──────────
        var totpKeyBase64 = configuration["Identity:TotpEncryptionKey"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_TOTP_ENCRYPTION_KEY");
        services.AddSingleton<IAesEncryptor>(_ =>
        {
            if (!string.IsNullOrWhiteSpace(totpKeyBase64))
            {
                try
                {
                    return AesEncryptor.FromBase64Key(totpKeyBase64);
                }
                catch
                {
                    // Fall through to dev fallback — logged in Program.cs startup.
                }
            }
            // Dev fallback: deterministic zero key so local dev doesn't crash.
            // Production startup validation (T044) must reject this.
            var devKey = new byte[32];
            return new AesEncryptor(devKey);
        });

        // ── Password + JWT + TOTP + password strength ────────────
        services.AddSingleton<IPasswordService, BCryptPasswordService>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<ITwoFactorService, TotpTwoFactorService>();
        services.AddSingleton<IPasswordStrengthValidator, ZxcvbnPasswordStrengthValidator>();

        // ── profile_ids claim resolution (generalized 1:1 profile map) ─
        // Every 1:1 domain profile that is keyed to a User registers one
        // IProfileIdContributor implementation. The aggregator emits the
        // union as the `profile_ids` JWT claim so the frontend reads
        // every domain id from a single source of truth.
        services.AddScoped<IProfileIdsResolver, ProfileIdsResolver>();
        services.AddScoped<IProfileIdContributor, StudentProfileIdContributor>();

        // ── Tenant + session services ────────────────────────────
        services.AddSingleton<ITenantResolutionService, TenantResolutionService>();
        services.AddScoped<ISessionRepository, EfSessionRepository>();
        services.AddScoped<ISessionService, SessionService>();

        // Replace the Phase-3 tenant accessor with the identity-aware one
        // so the JWT tenant claim is picked up by the EF global filter.
        services.AddScoped<IDbTenantContextAccessor, IdentityAwareTenantContextAccessor>();

        // ── Redis (rate-limit + session-activity cache) ──────────
        var redisConn = configuration["Redis:ConnectionString"]
            ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConn));
            services.AddSingleton<IRateLimitService, RedisRateLimitService>();
            services.AddSingleton<ISessionActivityCache, RedisSessionActivityCache>();
        }
        else
        {
            services.AddSingleton<IRateLimitService, NullRateLimitService>();
            services.AddSingleton<ISessionActivityCache, InMemorySessionActivityCache>();
        }

        // ── Notifications (identity-specific facade) ─────────────
        services.AddScoped<IIdentityNotificationSender, IdentityNotificationSender>();

        // ── Mailpit SMTP (dev only) ───────────────────────────────
        // When Mailpit:Enabled = true (appsettings.Development.json), this
        // adapter is registered after the Phase 4 HTTP stub so it wins for
        // the "email" channel in the registry (last-write-wins dict).
        // In production the key is absent — stub remains active.
        var mailpitEnabled = configuration["Mailpit:Enabled"]
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        if (mailpitEnabled)
        {
            var mailpitHost = configuration["Mailpit:SmtpHost"] ?? "localhost";
            var mailpitPort = int.TryParse(configuration["Mailpit:SmtpPort"], out var mp) ? mp : 1025;
            var mailpitFrom = configuration["Mailpit:FromAddress"] ?? "noreply@muallimi.local";
            services.AddSingleton<INotificationChannelAdapter>(sp =>
                new MailpitSmtpEmailAdapter(
                    new MailpitSmtpOptions
                    {
                        Host = mailpitHost,
                        Port = mailpitPort,
                        FromAddress = mailpitFrom,
                        FromName = "Muaallimi",
                    },
                    sp.GetRequiredService<ILogger<MailpitSmtpEmailAdapter>>()));
        }

        // ── US1: auth pipeline (T071–T078) ────────────────────────
        var verificationBase = configuration["Identity:VerificationBaseUrl"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_VERIFICATION_BASE_URL")
            ?? "http://localhost:3000";
        services.AddSingleton<IVerificationLinkBuilder>(
            _ => new VerificationLinkBuilder(verificationBase));
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        services.AddScoped<IAuthService, AuthService>();

        // Command validators
        services.AddScoped<ICommandValidator<RegisterParentCommand>, RegisterParentCommandValidator>();
        services.AddScoped<ICommandValidator<RegisterSchoolAdminCommand>, RegisterSchoolAdminCommandValidator>();
        services.AddScoped<ICommandValidator<LoginCommand>, LoginCommandValidator>();
        services.AddScoped<ICommandValidator<RefreshTokenCommand>, RefreshTokenCommandValidator>();
        services.AddScoped<ICommandValidator<LogoutCommand>, LogoutCommandValidator>();
        services.AddScoped<ICommandValidator<VerifyEmailCommand>, VerifyEmailCommandValidator>();
        services.AddScoped<ICommandValidator<ResendVerificationCommand>, ResendVerificationCommandValidator>();

        // US2: parent-children management (T087–T095)
        services.AddSingleton<IUsernameGenerator, UsernameGenerator>();
        services.AddSingleton<IChildPasswordGenerator, ChildPasswordGenerator>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<ICommandValidator<CreateChildCommand>, CreateChildCommandValidator>();
        services.AddScoped<ICommandValidator<UpdateChildCommand>, UpdateChildCommandValidator>();
        services.AddScoped<ICommandValidator<RegenerateChildPasswordCommand>, RegenerateChildPasswordCommandValidator>();
        services.AddScoped<ICommandValidator<DeleteChildCommand>, DeleteChildCommandValidator>();

        // US5: parent oversight (T141–T150)
        services.AddScoped<ICommandValidator<SuspendChildCommand>, SuspendChildCommandValidator>();
        services.AddScoped<ICommandValidator<UnsuspendChildCommand>, UnsuspendChildCommandValidator>();
        services.AddScoped<ICommandValidator<RevokeChildSessionCommand>, RevokeChildSessionCommandValidator>();
        services.AddScoped<IUnusualLoginDetector, UnusualLoginDetector>();

        // US3: admin user management + audit log + forced-rotation gate (T108–T114)
        services.AddSingleton<IInvitationLinkBuilder>(
            _ => new InvitationLinkBuilder(verificationBase));
        services.AddScoped<IAdminUserService, AdminUserService>();
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<ICommandValidator<InviteUserCommand>, InviteUserCommandValidator>();
        services.AddScoped<ICommandValidator<GrantRoleCommand>, GrantRoleCommandValidator>();
        services.AddScoped<ICommandValidator<RevokeRoleCommand>, RevokeRoleCommandValidator>();
        services.AddScoped<ICommandValidator<SuspendUserCommand>, SuspendUserCommandValidator>();
        services.AddScoped<ICommandValidator<UnsuspendUserCommand>, UnsuspendUserCommandValidator>();
        services.AddScoped<ICommandValidator<DeleteUserCommand>, DeleteUserCommandValidator>();
        services.AddScoped<ICommandValidator<AdminResetPasswordCommand>, AdminResetPasswordCommandValidator>();
        services.AddScoped<ICommandValidator<ChangePasswordCommand>, ChangePasswordCommandValidator>();
        services.AddScoped<ICommandValidator<AcceptInvitationCommand>, AcceptInvitationCommandValidator>();

        // US4: password + session self-service + 2FA (T122-T140)
        var resetBase = configuration["Identity:PasswordResetBaseUrl"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_PASSWORD_RESET_BASE_URL")
            ?? verificationBase; // fallback to same base URL as verification
        services.AddSingleton<IPasswordResetLinkBuilder>(_ => new PasswordResetLinkBuilder(resetBase));
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<ITwoFactorManagementService, TwoFactorManagementService>();
        services.AddScoped<ICommandValidator<ForgotPasswordCommand>, ForgotPasswordCommandValidator>();
        services.AddScoped<ICommandValidator<ResetPasswordCommand>, ResetPasswordCommandValidator>();
        services.AddScoped<ICommandValidator<EnableTwoFactorCommand>, EnableTwoFactorCommandValidator>();
        services.AddScoped<ICommandValidator<VerifyTwoFactorCommand>, VerifyTwoFactorCommandValidator>();
        services.AddScoped<ICommandValidator<DisableTwoFactorCommand>, DisableTwoFactorCommandValidator>();
        services.AddScoped<ICommandValidator<RevokeSessionCommand>, RevokeSessionCommandValidator>();

        // ── US6 Impersonation (T154 / T156 / T158) ───────────────
        services.AddScoped<IImpersonationService, ImpersonationService>();
        services.AddScoped<IImpersonationContext, HttpImpersonationContext>();

        // ── Seeders ───────────────────────────────────────────────
        services.AddScoped<RoleSeeder>();
        services.AddScoped<TenantSeeder>();
        services.AddScoped<SuperAdminSeeder>();
        services.AddSingleton<IdentitySeedRunner>();

        // ── US7: legacy backfill (T165) ───────────────────────────
        services.AddScoped<BackfillScriptRunner>();

        // ── Authorization filter (endpoint-filter style) ─────────
        services.AddSingleton<IdentityAuthorizationFilter>();

        // ── CORS policy (T041) ────────────────────────────────────
        var origins = (configuration["Identity:Cors:AllowedOrigins"]
            ?? Environment.GetEnvironmentVariable("IDENTITY_CORS_ALLOWED_ORIGINS")
            ?? "http://localhost:3000")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        services.AddCors(options =>
        {
            options.AddPolicy(IdentityCorsPolicy, policy => policy
                .WithOrigins(origins)
                .AllowCredentials()
                .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                .WithHeaders("Authorization", "Content-Type", "X-Correlation-Id", TenantResolutionMiddleware.TenantOverrideHeader, TenantResolutionMiddleware.LegacyTenantHeader));
        });

        // Identity middleware needs IHttpContextAccessor (Phase 3 adds it too,
        // but idempotent). Calling it here means AddIdentityModule is callable
        // standalone from tests.
        services.AddHttpContextAccessor();

        return services;
    }
}
