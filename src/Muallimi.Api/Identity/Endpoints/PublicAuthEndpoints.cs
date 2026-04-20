using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;

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
    public const string RefreshRoute = "/refresh";
    public const string VerifyEmailRoute = "/verify-email";
    public const string ResendVerificationRoute = "/resend-verification";

    public static RouteGroupBuilder MapPublicAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(RegisterRoute, HandleRegisterParentAsync);
        group.MapPost(RegisterParentRoute, HandleRegisterParentAsync);
        group.MapPost(RegisterSchoolAdminRoute, HandleRegisterSchoolAdminAsync);
        group.MapPost(LoginRoute, HandleLoginAsync);
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
            CorrelationId: correlationId);
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
            CorrelationId: correlationId);
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

    [System.Text.Json.Serialization.JsonPropertyName("acceptedTerms")]
    public bool AcceptedTerms { get; set; }
}
