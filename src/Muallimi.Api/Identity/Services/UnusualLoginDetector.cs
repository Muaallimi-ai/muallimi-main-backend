using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T145 — EF Core implementation of IUnusualLoginDetector. Lives in Api layer
/// (not Application) because it directly queries MuallimiDbContext.
/// Unusual criteria (OR):
///   1. User-agent string not seen in prior 30-day successful logins.
///   2. Incoming IPv4 /24 or IPv6 /48 prefix not seen in the same window.
/// First login ever → not flagged (no baseline to compare against).
/// </summary>
public sealed class UnusualLoginDetector : IUnusualLoginDetector
{
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(30);

    private readonly MuallimiDbContext _db;

    public UnusualLoginDetector(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<bool> RecordAndDetectAsync(
        Guid userId,
        string ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - LookbackWindow;

        var priorLogins = await _db.IdentityLoginAttempts
            .IgnoreQueryFilters()
            .Where(la => la.UserId == userId
                && la.Outcome == LoginOutcome.Success
                && la.AttemptedAt >= since)
            .Select(la => new { la.IpAddress, la.UserAgent })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Record this successful login for future comparisons.
        _db.IdentityLoginAttempts.Add(new Domain.Identity.Entities.LoginAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = string.Empty,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Outcome = LoginOutcome.Success,
            AttemptedAt = DateTime.UtcNow,
        });
        // Caller must SaveChanges.

        if (priorLogins.Count == 0) return false;

        var newPrefix = ExtractPrefix(ipAddress);
        var newUa = NormalizeUa(userAgent);

        var knownPrefixes = priorLogins
            .Select(l => ExtractPrefix(l.IpAddress))
            .Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var knownUas = priorLogins
            .Select(l => NormalizeUa(l.UserAgent))
            .Where(ua => ua is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var ipIsNew = newPrefix is null || !knownPrefixes.Contains(newPrefix);
        var uaIsNew = newUa is null || !knownUas.Contains(newUa);

        return ipIsNew || uaIsNew;
    }

    private static string? ExtractPrefix(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (IPAddress.TryParse(ip, out var addr))
        {
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = addr.GetAddressBytes();
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
            }
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = addr.GetAddressBytes();
                return BitConverter.ToString(bytes, 0, 6);
            }
        }
        return ip;
    }

    private static string? NormalizeUa(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        return ua.Length > 80 ? ua[..80] : ua;
    }
}
