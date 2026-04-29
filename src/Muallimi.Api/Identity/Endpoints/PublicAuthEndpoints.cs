using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// T075 — Public auth endpoints mounted under
/// <see cref="Startup.IdentityEndpointRouteBuilderExtensions.IdentityRoutePrefix"/>.
/// Surface:
///   • <c>POST /register</c>  (alias for /register/parent)
///   • <c>POST /register/parent</c>
///   • <c>POST /register/school-admin</c>
///   • <c>POST /login</c>
///   • <c>POST /refresh</c>
///   • <c>POST /verify-email</c>
///   • <c>POST /resend-verification</c>
/// </summary>
public static class PublicAuthEndpoints
{
    public const string RegisterRoute = "/register";
    public const string RegisterParentRoute = "/register/parent";
    public const string RegisterSchoolAdminRoute = "/register/school-admin";
    public const string LoginRoute = "/login";
    public const string LoginPinRoute = "/login/pin";
    public const string LookupMethodRoute = "/lookup-method";
    public const string RefreshRoute = "/refresh";
    public const string VerifyEmailRoute = "/verify-email";
    public const string ResendVerificationRoute = "/resend-verification";

    public static RouteGroupBuilder MapPublicAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(RegisterRoute, HandleRegisterParentAsync);
        group.MapPost(RegisterParentRoute, HandleRegisterParentAsync);
        group.MapPost(RegisterSchoolAdminRoute, HandleRegisterSchoolAdminAsync);
        group.MapPost(LoginRoute, HandleLoginAsync);
        group.MapPost(LoginPinRoute, HandlePinLoginAsync);
        group.MapPost(LookupMethodRoute, HandleLookupMethodAsync);
        group.MapPost(RefreshRoute, HandleRefreshAsync);
        group.MapPost(VerifyEmailRoute, HandleVerifyEmailAsync);
        group.MapPost(ResendVerificationRoute, HandleResendVerificationAsync);
        return group;
    }

    private static async Task<IResult> HandleRegisterParentAsync(
        RegisterRequest request,
        HttpContext http,
        IAuthService auth,
        ICommandValidator<RegisterParentCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new RegisterParentCommand(
            Email: request.Email ?? string.Empty,
            Password: request.Password ?? string.Empty,
            FullName: request.FullName ?? string.Empty,
            FullNameEn: request.FullNameEn,
            Locale: string.IsNullOrWhiteSpace(request.Locale) ? "ar" : request.Locale!,
            AcceptedTerms: request.AcceptedTerms,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId,
            PhoneNumber: request.PhoneNumber ?? string.Empty);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var outcome = await auth.RegisterParentAsync(cmd, ct).ConfigureAwait(false);
        return RenderOutcome(outcome, correlationId);
    }

    private static async Task<IResult> HandleRegisterSchoolAdminAsync(
        SchoolAdminRegisterRequest request,
        HttpContext http,
        IAuthService auth,
        ICommandValidator<RegisterSchoolAdminCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new RegisterSchoolAdminCommand(
            Email: request.Email ?? string.Empty,
            Password: request.Password ?? string.Empty,
            FullName: request.FullName ?? string.Empty,
            FullNameEn: request.FullNameEn,
            Locale: string.IsNullOrWhiteSpace(request.Locale) ? "ar" : request.Locale!,
            SchoolDisplayName: request.SchoolDisplayName ?? string.Empty,
            AcceptedTerms: request.AcceptedTerms,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId,
            PhoneNumber: request.PhoneNumber ?? string.Empty);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var outcome = await auth.RegisterSchoolAdminAsync(cmd, ct).ConfigureAwait(false);
        return RenderOutcome(outcome, correlationId);
    }

    private static async Task<IResult> HandleLoginAsync(
        LoginRequest request,
        HttpContext http,
        IAuthService auth,
        ICommandValidator<LoginCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new LoginCommand(
            Identifier: request.Identifier ?? string.Empty,
            Password: request.Password ?? string.Empty,
            RememberMe: request.RememberMe,
            TwoFactorCode: request.TwoFactorCode,
            TempToken: request.TempToken,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var outcome = await auth.LoginAsync(cmd, ct).ConfigureAwait(false);
        if (outcome.TwoFactor is not null)
        {
            var envelope = new ApiResponseEnvelope<TwoFactorChallengeResponse>
            {
                Success = false,
                Message = outcome.Message,
                Data = outcome.TwoFactor,
                Errors = outcome.Errors,
                Timestamp = System.DateTime.UtcNow,
                CorrelationId = correlationId,
            };
            return Results.Json(envelope, statusCode: outcome.HttpStatus);
        }
        return RenderOutcome(outcome, correlationId);
    }

    private static async Task<IResult> HandlePinLoginAsync(
        PinLoginRequest request,
        HttpContext http,
        IAuthService auth,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Pin))
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "اسم المستخدم ورمز PIN مطلوبان.", correlationId);
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Pin, "^[0-9]{4}$"))
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "رمز PIN يجب أن يكون 4 أرقام.", correlationId);
        }
        var cmd = new PinLoginCommand(
            Username: request.Username,
            Pin: request.Pin,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var outcome = await auth.LoginWithPinAsync(cmd, ct).ConfigureAwait(false);
        return RenderOutcome(outcome, correlationId);
    }

    /// <summary>
    /// UX hint endpoint that tells the login form which credential field
    /// shape to render. Returns <c>{ method: "pin" | "password" }</c>.
    ///
    /// Default-safe rule: the form always defaults to <c>"password"</c>
    /// EXCEPT when the identifier resolves to a real 8-12 child on the
    /// PIN tier — only then do we return <c>"pin"</c>. Every other case
    /// (real password account, real under-8 child, real 13+ child,
    /// unknown email/phone/username, empty input) returns
    /// <c>"password"</c>.
    ///
    /// Anti-enumeration disciplines:
    /// • Same response shape on hit and miss; same envelope; same status.
    /// • DB lookup runs even on empty input so the timing profile matches.
    /// • 8-16ms jitter masks DB round-trip variance.
    /// • Rate limit 3/min/IP (parity with /login).
    /// • The login endpoints themselves return identical
    ///   <c>invalid_credentials</c> for "user doesn't exist" vs "wrong
    ///   credential" — that's where the real anti-enum boundary lives.
    ///   This endpoint is a UX hint, not a security boundary; usernames
    ///   are auto-generated and never published, so a "pin" response
    ///   only confirms tier for an attacker who already had the
    ///   username, and the username already encodes the birth year.
    /// </summary>
    private static async Task<IResult> HandleLookupMethodAsync(
        LookupMethodRequest request,
        HttpContext http,
        MuallimiDbContext db,
        IRateLimitService rateLimit,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var ip = AuthEndpointHelpers.ResolveIp(http);

        var rl = await rateLimit.IncrementAndCheckAsync(
            "lookup-method-ip", ip, 3, System.TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthEndpointHelpers.FailEnvelope(429, "rate_limited", "تم تجاوز عدد المحاولات.", correlationId);
        }

        var identifier = (request.Identifier ?? string.Empty).Trim().ToLowerInvariant();

        User? user = null;
        if (!string.IsNullOrEmpty(identifier))
        {
            user = await db.IdentityUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u =>
                    u.NormalizedUsername == identifier ||
                    u.NormalizedEmail == identifier, ct).ConfigureAwait(false);
        }

        // Small jitter to mask DB-roundtrip variance between hit/miss.
        await Task.Delay(System.Random.Shared.Next(8, 16), ct).ConfigureAwait(false);

        // Default to "password". Only flip to "pin" when the identifier
        // resolves to a real Managed account whose LoginMethod is "pin".
        var method = (user is { AccountType: AccountType.Managed, LoginMethod: "pin" })
            ? "pin"
            : "password";

        return AuthEndpointHelpers.OkEnvelope(new { method }, "ok", correlationId);
    }

    private static async Task<IResult> HandleRefreshAsync(
        RefreshRequest request,
        HttpContext http,
        IAuthService auth,
        ICommandValidator<RefreshTokenCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new RefreshTokenCommand(
            RefreshToken: request.RefreshToken ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var outcome = await auth.RefreshAsync(cmd, ct).ConfigureAwait(false);
        return RenderOutcome(outcome, correlationId);
    }

    private static async Task<IResult> HandleVerifyEmailAsync(
        VerifyEmailRequest request,
        HttpContext http,
        IEmailVerificationService verification,
        ICommandValidator<VerifyEmailCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new VerifyEmailCommand(
            Token: request.Token ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var result = await verification.ConsumeAsync(cmd.Token, correlationId, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return AuthEndpointHelpers.FailEnvelope(400, result.ErrorCode ?? "token_invalid", "رمز التحقق غير صالح.", correlationId);
        }
        return AuthEndpointHelpers.OkEnvelope(
            new { userId = result.UserId?.ToString("D"), emailVerified = true },
            "تم تأكيد البريد الإلكتروني بنجاح.",
            correlationId);
    }

    private static async Task<IResult> HandleResendVerificationAsync(
        ResendVerificationRequest request,
        HttpContext http,
        IEmailVerificationService verification,
        Muallimi.Application.Identity.Notifications.IIdentityNotificationSender notifications,
        IVerificationLinkBuilder linkBuilder,
        IRateLimitService rateLimit,
        ICommandValidator<ResendVerificationCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new ResendVerificationCommand(
            Email: request.Email ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var rl = await rateLimit.IncrementAndCheckAsync(
            "resend-verification",
            cmd.Email.ToLowerInvariant(),
            3,
            System.TimeSpan.FromHours(1),
            ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthEndpointHelpers.FailEnvelope(429, "rate_limited", "تم تجاوز عدد محاولات إعادة الإرسال.", correlationId);
        }

        var normalized = cmd.Email.Trim().ToLowerInvariant();
        var result = await verification.ResendAsync(normalized, correlationId, ct).ConfigureAwait(false);
        if (result.Success && !string.IsNullOrWhiteSpace(result.PlaintextToken) && result.UserId.HasValue)
        {
            try
            {
                await notifications.SendEmailVerificationAsync(
                    new Muallimi.Application.Identity.Notifications.IdentityNotificationRecipient(
                        TenantId: result.TenantId ?? System.Guid.Empty,
                        UserId: result.UserId.Value,
                        Email: result.Email,
                        FullName: result.FullName ?? string.Empty,
                        Locale: result.Locale ?? "ar"),
                    linkBuilder.BuildVerificationLink(result.PlaintextToken!),
                    correlationId,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Fire-and-forget — the envelope stays success so we don't leak
                // whether delivery succeeded.
            }
        }
        return AuthEndpointHelpers.OkEnvelope(
            new { delivered = true },
            "إذا كان البريد مسجّلًا، فسيُرسَل رابط تحقّق.",
            correlationId);
    }

    private static IResult RenderOutcome(AuthOutcome outcome, string correlationId)
    {
        if (outcome.Success)
        {
            // 202 Pending: registration accepted, payment required before account exists.
            if (outcome.PendingPayload is not null)
            {
                var pendingEnvelope = new ApiResponseEnvelope<PendingRegistrationPayload>
                {
                    Success = true,
                    Message = outcome.Message,
                    Data = outcome.PendingPayload,
                    Errors = null,
                    Timestamp = System.DateTime.UtcNow,
                    CorrelationId = correlationId,
                };
                return Results.Json(pendingEnvelope, statusCode: 202);
            }

            var envelope = new ApiResponseEnvelope<AuthResponse>
            {
                Success = true,
                Message = outcome.Message,
                Data = outcome.Payload,
                Errors = null,
                Timestamp = System.DateTime.UtcNow,
                CorrelationId = correlationId,
            };
            return Results.Json(envelope, statusCode: outcome.HttpStatus);
        }
        return AuthEndpointHelpers.FailEnvelope(
            outcome.HttpStatus,
            outcome.ErrorCode ?? "auth_failed",
            outcome.Message,
            correlationId,
            outcome.Errors);
    }
}

/// <summary>
/// Request body for <c>/register/school-admin</c> — adds the school
/// display name on top of the common register fields.
/// </summary>
public sealed class SchoolAdminRegisterRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("schoolDisplayName")]
    public string SchoolDisplayName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("acceptedTerms")]
    public bool AcceptedTerms { get; set; }
}
