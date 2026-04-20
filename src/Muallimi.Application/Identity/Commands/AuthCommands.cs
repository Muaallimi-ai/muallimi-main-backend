using System;
using System.Collections.Generic;
using Muallimi.Application.Identity.Dtos;

namespace Muallimi.Application.Identity.Commands;

/// <summary>
/// T073 — Internal commands consumed by <c>AuthService</c>.
/// Richer than the wire <see cref="RegisterRequest"/>/<see cref="LoginRequest"/>
/// DTOs because the service needs the request context (IP, user agent,
/// correlation id, device descriptor).
///
/// FluentValidation would normally carry these, but the repo does not
/// currently take a FluentValidation dependency; the accompanying
/// <c>*Validator</c> classes (see <c>AuthCommandValidators.cs</c>)
/// emit the same <see cref="ApiResponseError"/> collection that the
/// envelope expects.
/// </summary>
public sealed record RegisterParentCommand(
    string Email,
    string Password,
    string FullName,
    string? FullNameEn,
    string Locale,
    bool AcceptedTerms,
    string IpAddress,
    string? UserAgent,
    string CorrelationId,
    string PhoneNumber = "");

public sealed record RegisterSchoolAdminCommand(
    string Email,
    string Password,
    string FullName,
    string? FullNameEn,
    string Locale,
    string SchoolDisplayName,
    bool AcceptedTerms,
    string IpAddress,
    string? UserAgent,
    string CorrelationId,
    string PhoneNumber = "");

public sealed record LoginCommand(
    string Identifier,
    string Password,
    bool RememberMe,
    string? TwoFactorCode,
    string? TempToken,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record RefreshTokenCommand(
    string RefreshToken,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record LogoutCommand(
    Guid SessionId,
    Guid UserId,
    string? RefreshToken,
    string CorrelationId);

public sealed record VerifyEmailCommand(
    string Token,
    string IpAddress,
    string CorrelationId);

public sealed record ResendVerificationCommand(
    string Email,
    string IpAddress,
    string CorrelationId);
