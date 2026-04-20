using System;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Append-only login attempt record, retained 90 days. Does not drive
/// authorization decisions — the lockout counter lives on <see cref="User"/>.
/// Used for brute-force detection and forensics only.
/// </summary>
public class LoginAttempt
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public LoginOutcome Outcome { get; set; }
    public string? FailureReason { get; set; }
    public DateTime AttemptedAt { get; set; } = DateTime.UtcNow;
}
