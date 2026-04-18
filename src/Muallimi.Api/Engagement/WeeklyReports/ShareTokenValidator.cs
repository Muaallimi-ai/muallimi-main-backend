using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T095 + T096 (US3) — Tenant-safe signed share tokens.
///
/// Format: <c>{weekly_report_id}.{tenant_id}.{expires_at_epoch}.{hmac}</c>
/// where <c>hmac</c> is <c>HMAC-SHA256</c> over
/// <c>{report}:{tenant}:{expires}</c> using a per-tenant key derived from
/// the main-backend share-link secret. The tenant id is signed into the
/// token so a token issued in one tenant cannot open a report in another
/// tenant (the signature fails because the per-tenant key differs).
///
/// The signed token is short-TTL by contract — <see cref="Issue"/>'s
/// caller chooses the TTL (default 72 hours). Tokens outside
/// <c>[now, now + max_ttl]</c> are rejected.
/// </summary>
public interface IShareTokenValidator
{
    SharedReportToken Issue(Guid tenantId, Guid weeklyReportId, TimeSpan ttl);
    bool TryParse(string rawToken, out SharedReportTokenClaims claims);
    string HashForStorage(string rawToken);
}

public sealed record SharedReportToken(string RawToken, DateTime ExpiresAt);

public sealed record SharedReportTokenClaims(Guid TenantId, Guid WeeklyReportId, DateTime ExpiresAt);

public sealed class ShareTokenValidator : IShareTokenValidator
{
    public const string DefaultSecret = "phase4-share-link-local-secret-v1";
    private const string KeyPrefix = "phase4-share-link::";
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(72);

    private readonly string _secret;

    public ShareTokenValidator(string? secret = null)
    {
        _secret = string.IsNullOrWhiteSpace(secret) ? DefaultSecret : secret!;
    }

    public SharedReportToken Issue(Guid tenantId, Guid weeklyReportId, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromHours(1);
        if (ttl > MaxTtl) ttl = MaxTtl;
        var expiresAt = DateTime.UtcNow.Add(ttl);
        var epoch = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        var payload = $"{weeklyReportId:D}:{tenantId:D}:{epoch}";
        var sig = Sign(tenantId, payload);
        var raw = $"{weeklyReportId:D}.{tenantId:D}.{epoch}.{sig}";
        return new SharedReportToken(raw, expiresAt);
    }

    public bool TryParse(string rawToken, out SharedReportTokenClaims claims)
    {
        claims = default!;
        if (string.IsNullOrWhiteSpace(rawToken)) return false;
        var parts = rawToken.Split('.');
        if (parts.Length != 4) return false;
        if (!Guid.TryParse(parts[0], out var reportId)) return false;
        if (!Guid.TryParse(parts[1], out var tenantId)) return false;
        if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)) return false;
        var providedSig = parts[3];

        var payload = $"{reportId:D}:{tenantId:D}:{epoch}";
        var expected = Sign(tenantId, payload);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedSig),
                Encoding.UTF8.GetBytes(expected)))
        {
            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        if (expiresAt <= DateTime.UtcNow) return false;
        if (expiresAt > DateTime.UtcNow.Add(MaxTtl).AddMinutes(1)) return false;

        claims = new SharedReportTokenClaims(tenantId, reportId, expiresAt);
        return true;
    }

    public string HashForStorage(string rawToken)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(rawToken));
        return ToUrlBase64(bytes);
    }

    private string Sign(Guid tenantId, string payload)
    {
        var keyMaterial = Encoding.UTF8.GetBytes($"{KeyPrefix}{_secret}:{tenantId:D}");
        using var hmac = new HMACSHA256(keyMaterial);
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return ToUrlBase64(sig);
    }

    private static string ToUrlBase64(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

public static class ShareTokenValidatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ShareTokenValidator(this IServiceCollection services)
    {
        services.AddSingleton<IShareTokenValidator>(_ => new ShareTokenValidator());
        return services;
    }
}
