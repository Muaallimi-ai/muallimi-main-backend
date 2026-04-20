using System;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T145 — Detects logins from previously-unseen devices or IP subnets.
/// Interface lives in Application; implementation in Muallimi.Api (needs EF Core).
/// </summary>
public interface IUnusualLoginDetector
{
    /// <summary>
    /// Records the successful login attempt and returns true if the login
    /// is from a previously-unseen context.
    /// </summary>
    Task<bool> RecordAndDetectAsync(
        Guid userId,
        string ipAddress,
        string? userAgent,
        CancellationToken ct = default);
}
