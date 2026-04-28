using System;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// User — human actor. Carries login identity, verification state,
/// lockout state, and soft-delete state. Personal vs Managed is the
/// fundamental branch: Personal accounts log in with email (parents,
/// school-admins, teachers, operators), Managed accounts log in with a
/// username and are managed by another user (students created by a
/// parent or by school roster import).
/// </summary>
public class User : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }

    public AccountType AccountType { get; set; }
    public Guid? ManagedByUserId { get; set; }

    public string? Username { get; set; }
    public string? NormalizedUsername { get; set; }
    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }

    public bool EmailVerified { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public string? PhoneNumber { get; set; }
    public bool PhoneVerified { get; set; }

    public string FullName { get; set; } = string.Empty;
    public string? FullNameEn { get; set; }

    public string? PasswordHash { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public bool RequiresPasswordReset { get; set; }

    /// <summary>
    /// Credential method for Managed (child) accounts.
    /// "username_password" (12+), "pin" (8–12), "avatar_only" (&lt;8).
    /// Null for Personal accounts.
    /// </summary>
    public string? LoginMethod { get; set; }

    /// <summary>Hashed 4-digit PIN. Set only when LoginMethod = "pin".</summary>
    public string? PinHash { get; set; }

    public bool TwoFactorEnabled { get; set; }
    public string Locale { get; set; } = "ar";

    public UserStatus Status { get; set; } = UserStatus.PendingEmailVerification;

    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEnd { get; set; }

    public DateTime? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // ── Invariant helpers ─────────────────────────────────────────────

    /// <summary>Caller must have validated identifier shape before invoking.</summary>
    public void AssertAccountTypeInvariants()
    {
        if (AccountType == AccountType.Personal)
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                throw new InvalidOperationException("Personal accounts require an email.");
            }
        }
        else if (AccountType == AccountType.Managed)
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                throw new InvalidOperationException("Managed accounts require a username.");
            }
            if (ManagedByUserId is null)
            {
                throw new InvalidOperationException("Managed accounts require ManagedByUserId.");
            }
        }
    }

    // ── State machine ─────────────────────────────────────────────────

    public void VerifyEmail()
    {
        if (Status != UserStatus.PendingEmailVerification)
        {
            throw new InvalidOperationException($"Only PendingEmailVerification accounts can verify (current: {Status}).");
        }
        EmailVerified = true;
        EmailVerifiedAt = DateTime.UtcNow;
        Status = UserStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RegisterFailedLogin(int lockoutThreshold, TimeSpan lockoutDuration)
    {
        FailedLoginAttempts += 1;
        if (FailedLoginAttempts >= lockoutThreshold && Status == UserStatus.Active)
        {
            Status = UserStatus.Locked;
            LockoutEnd = DateTime.UtcNow.Add(lockoutDuration);
        }
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Add-child redesign security non-negotiable #3: PIN lockout is
    /// permanent (LockoutEnd = null) — parent must unlock via
    /// <c>POST /api/parent/children/{id}/unlock</c>. There is no
    /// time-based auto-recovery on PIN like there is on password.
    /// </summary>
    public void RegisterFailedPinLogin(int lockoutThreshold)
    {
        FailedLoginAttempts += 1;
        if (FailedLoginAttempts >= lockoutThreshold && Status == UserStatus.Active)
        {
            Status = UserStatus.Locked;
            LockoutEnd = null;
        }
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkSuccessfulLogin(string ip)
    {
        if (Status == UserStatus.Locked && LockoutEnd is { } end && end <= DateTime.UtcNow)
        {
            Status = UserStatus.Active;
        }
        if (Status != UserStatus.Active && Status != UserStatus.PasswordResetRequired)
        {
            throw new InvalidOperationException($"Login is not allowed in status {Status}.");
        }
        FailedLoginAttempts = 0;
        LockoutEnd = null;
        LastLoginAt = DateTime.UtcNow;
        LastLoginIp = ip;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Suspend()
    {
        if (Status == UserStatus.Archived)
        {
            throw new InvalidOperationException("Archived users cannot be suspended.");
        }
        Status = UserStatus.Suspended;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Unsuspend()
    {
        if (Status != UserStatus.Suspended)
        {
            throw new InvalidOperationException($"Only Suspended users can be unsuspended (current: {Status}).");
        }
        Status = UserStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RequirePasswordReset()
    {
        if (Status == UserStatus.Archived)
        {
            throw new InvalidOperationException("Archived users cannot require password reset.");
        }
        Status = UserStatus.PasswordResetRequired;
        RequiresPasswordReset = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void CompletePasswordReset(string newHash)
    {
        if (Status == UserStatus.Archived)
        {
            throw new InvalidOperationException("Archived users cannot reset password.");
        }
        PasswordHash = newHash;
        PasswordChangedAt = DateTime.UtcNow;
        RequiresPasswordReset = false;
        FailedLoginAttempts = 0;
        LockoutEnd = null;
        if (Status == UserStatus.Locked || Status == UserStatus.PasswordResetRequired)
        {
            Status = UserStatus.Active;
        }
        UpdatedAt = DateTime.UtcNow;
    }

    public void Archive()
    {
        Status = UserStatus.Archived;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
