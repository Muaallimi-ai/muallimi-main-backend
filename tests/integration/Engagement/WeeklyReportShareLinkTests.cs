using System;
using System.Threading.Tasks;
using Muallimi.Api.Engagement.WeeklyReports;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T088 (US3) — Integration test for the tenant-safe share link.
///
/// Tokens are signed per-tenant. A token issued under tenant A MUST NOT
/// parse back to tenant B, even if an attacker guesses the report id.
/// Tokens expire on the documented TTL and parse back to the exact
/// report id they were issued for.
/// </summary>
public class WeeklyReportShareLinkTests
{
    [Fact]
    public void Token_Issued_Under_Tenant_A_Does_Not_Validate_Under_Tenant_B()
    {
        var tokens = new ShareTokenValidator();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var reportId = Guid.NewGuid();

        var issued = tokens.Issue(tenantA, reportId, TimeSpan.FromHours(1));
        Assert.True(tokens.TryParse(issued.RawToken, out var claimsA));
        Assert.Equal(tenantA, claimsA.TenantId);
        Assert.Equal(reportId, claimsA.WeeklyReportId);

        // Tamper: swap the tenant id in the token to tenantB.
        var parts = issued.RawToken.Split('.');
        var forged = $"{parts[0]}.{tenantB:D}.{parts[2]}.{parts[3]}";
        Assert.False(tokens.TryParse(forged, out _));
    }

    [Fact]
    public void Expired_Tokens_Fail_To_Parse()
    {
        var tokens = new ShareTokenValidator();
        var tenantId = Guid.NewGuid();
        var reportId = Guid.NewGuid();

        var issued = tokens.Issue(tenantId, reportId, TimeSpan.FromMilliseconds(1));
        System.Threading.Thread.Sleep(1_100);
        Assert.False(tokens.TryParse(issued.RawToken, out _));
    }

    [Fact]
    public void Token_Hash_Stored_On_Report_Row_Matches_On_Lookup()
    {
        var tokens = new ShareTokenValidator();
        var issued = tokens.Issue(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromHours(1));
        var hashA = tokens.HashForStorage(issued.RawToken);
        var hashB = tokens.HashForStorage(issued.RawToken);
        Assert.Equal(hashA, hashB);
        Assert.NotEmpty(hashA);
    }

    [Fact]
    public async Task Shared_Report_Route_Only_Opens_The_Specific_Report_Stored_With_That_Hash()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end, "corr-share", forceRegenerate: false);
        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);

        var issued = harness.Tokens.Issue(tenantId, report!.WeeklyReportId, TimeSpan.FromHours(1));
        report.ShareTokenHash = harness.Tokens.HashForStorage(issued.RawToken);
        await harness.Reports.UpdateAsync(report);
        await harness.Db.SaveChangesAsync();

        Assert.True(harness.Tokens.TryParse(issued.RawToken, out var claims));
        Assert.Equal(report.WeeklyReportId, claims.WeeklyReportId);
        var stored = await harness.Reports.GetByIdAsync(claims.TenantId, claims.WeeklyReportId);
        Assert.NotNull(stored);
        Assert.Equal(report.ShareTokenHash, stored!.ShareTokenHash);
    }
}
