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
    /// Optimistic-concurrency token for credential mutations. Increments
    /// on every <see cref="SetPassword"/> and <see cref="SetPin"/> call.
    /// Marked <c>IsConcurrencyToken</c> in the EF configuration so two
    /// simultaneous credential changes (e.g. child self-change racing
    /// parent reset) cannot silently overwrite each other — the loser
    /// gets a <see cref="DbUpdateConcurrencyException"/> which the API
    /// layer translates to HTTP 409.
    /// </summary>
    public int PasswordHashVersion { get; set; }

    /// <summary>
    /// Credential method for Managed (child) accounts.
    /// "username_password" (12+), "pin" (8–12), "profile_switch_only" (&lt;8).
    /// Null for Personal accounts.
    /// </summary>
    public string? LoginMethod { get; set; }

    /// <summary>Hashed 4-digit PIN. Set only when LoginMethod = "pin".</summary>
    public string? PinHash { get; set; }

    /// <summary>
    /// Set by <see cref="ChildAgeTransitionJob"/> on the day the child
    /// reaches a credential-tier threshold (8 → eligible for PIN, 13 →
    /// eligible for password). Idempotency flag — once set, the daily
    /// job never re-notifies the parent for the same child. Null until
    /// the first transition fires.
    /// </summary>
    public DateTime? AgeTransitionNotifiedAt { get; set; }

    /// <summary>
    /// Set when the parent resets this child's password or PIN — the
    /// child sees a one-time informational notice on their next
    /// successful login ("your password was reset by your parent on
    /// [date]") and the field is cleared after the notice is shown.
    /// Never set on parent-self-reset of an own credential.
    /// </summary>
    public DateTime? PendingParentResetNoticeAt { get; set; }

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

    /// <summary>
    /// Canonical password mutation. Sets the hash, stamps
    /// <see cref="PasswordChangedAt"/>, bumps
    /// <see cref="PasswordHashVersion"/> for optimistic concurrency, and
    /// clears <see cref="RequiresPasswordReset"/>. Does NOT touch
    /// lockout state or status — callers compose lockout/status changes
    /// explicitly when relevant (e.g. <see cref="CompletePasswordReset"/>
    /// is the reset-flow composition; child self-change keeps the
    /// current session and only revokes other sessions externally).
    /// </summary>
    public void SetPassword(string newHash)
    {
        if (Status == UserStatus.Archived)
        {
            throw new InvalidOperationException("Archived users cannot change password.");
        }
        PasswordHash = newHash;
        PasswordChangedAt = DateTime.UtcNow;
        RequiresPasswordReset = false;
        PasswordHashVersion += 1;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Canonical PIN mutation. Sets the PIN hash and bumps
    /// <see cref="PasswordHashVersion"/> — the same concurrency token
    /// covers both credential types, so a parent reset of a PIN cannot
    /// race a parent reset of a password (or vice versa) on the same
    /// account. Does NOT touch lockout state — PIN unlock is an
    /// explicit parent action, see <see cref="UnlockPin"/>.
    /// </summary>
    public void SetPin(string newPinHash)
    {
        if (Status == UserStatus.Archived)
        {
            throw new InvalidOperationException("Archived users cannot change PIN.");
        }
        PinHash = newPinHash;
        PasswordHashVersion += 1;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Reset-flow composition: applies <see cref="SetPassword"/>, clears
    /// failed-login state, and transitions Locked / PasswordResetRequired
    /// back to Active. Used by parent/admin reset endpoints and the
    /// self-service email reset link.
    /// </summary>
    public void CompletePasswordReset(string newHash)
    {
        SetPassword(newHash);
        FailedLoginAttempts = 0;
        LockoutEnd = null;
        if (Status == UserStatus.Locked || Status == UserStatus.PasswordResetRequired)
        {
            Status = UserStatus.Active;
        }
    }

    /// <summary>
    /// Stamp the post-reset notice marker so this child sees a
    /// one-time informational message on next login. Called by parent
    /// reset flows (password / PIN). No-op for parent-self-reset.
    /// </summary>
    public void MarkPendingParentResetNotice()
    {
        PendingParentResetNoticeAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Clear the post-reset notice marker after the child has seen
    /// the one-time notice on a successful login.
    /// </summary>
    public void AcknowledgeParentResetNotice()
    {
        if (PendingParentResetNoticeAt is null) return;
        PendingParentResetNoticeAt = null;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Promote a profile-switch-only (under-8) child to PIN tier on
    /// their 8th birthday. Sets the credential string and the PIN hash
    /// in one atomic mutation that bumps <see cref="PasswordHashVersion"/>.
    /// </summary>
    public void AddPinForUnderEight(string newPinHash)
    {
        if (LoginMethod != Enums.LoginMethods.ProfileSwitchOnly)
        {
            throw new InvalidOperationException($"AddPinForUnderEight requires LoginMethod={Enums.LoginMethods.ProfileSwitchOnly} (current: {LoginMethod}).");
        }
        LoginMethod = Enums.LoginMethods.Pin;
        SetPin(newPinHash);
    }

    /// <summary>
    /// Promote a PIN-tier (8–12) child to username + password tier on
    /// their 13th birthday. Clears the PinHash, sets the password hash,
    /// and bumps <see cref="PasswordHashVersion"/> in one mutation so
    /// the tier change is atomic with the credential rotation.
    /// </summary>
    public void UpgradePinToPassword(string newPasswordHash)
    {
        if (LoginMethod != Enums.LoginMethods.Pin)
        {
            throw new InvalidOperationException($"UpgradePinToPassword requires LoginMethod={Enums.LoginMethods.Pin} (current: {LoginMethod}).");
        }
        LoginMethod = Enums.LoginMethods.UsernamePassword;
        PinHash = null;
        SetPassword(newPasswordHash);
    }

    /// <summary>
    /// Explicit parent-driven PIN unlock. PIN lockouts are permanent
    /// (no time-based auto-recovery) — this is the only path back to
    /// <see cref="UserStatus.Active"/> for a child that hit the PIN
    /// failure threshold.
    /// </summary>
    public void UnlockPin()
    {
        if (Status != UserStatus.Locked)
        {
            throw new InvalidOperationException($"Only Locked users can be unlocked (current: {Status}).");
        }
        FailedLoginAttempts = 0;
        LockoutEnd = null;
        Status = UserStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Archive()
    {
        Status = UserStatus.Archived;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
