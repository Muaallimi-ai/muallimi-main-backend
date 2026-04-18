using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.AdminOnboarding;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolTenant;

/// <summary>
/// T030 (US1) — Contract test for POST <c>/operator/schools/{id}/admins</c>.
///
/// Pins the invite endpoint route, request shape, and the <c>invited</c>
/// onboarding status returned for fresh administrator rows.
/// </summary>
public class InviteAdminTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/operator/schools/{schoolTenantId:guid}/admins", AdminOnboardingEndpoints.InviteRoute);
    }

    [Fact]
    public void Request_Shape_Matches_Contract()
    {
        var props = PropertyNamesOf<AdminOnboardingEndpoints.InviteAdminRequest>();
        Assert.Contains("email", props);
        Assert.Contains("display_name_ar", props);
        Assert.Contains("display_name_en", props);
    }

    [Fact]
    public async Task Invite_Writes_Row_With_Invited_Status()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolAdminRepository(db);
        var service = new AdminOnboardingService(repo);

        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var admin = await service.InviteAsync(
            new AdminInviteInput(
                TenantId: tenantId,
                SchoolTenantId: schoolTenantId,
                InvitationEmail: "admin@example.test",
                DisplayNameAr: "المدير",
                DisplayNameEn: "The Admin"),
            CancellationToken.None);

        Assert.Equal("invited", admin.OnboardingStatus);
        Assert.Null(admin.TermsAcceptedAt);
        Assert.Equal(schoolTenantId, admin.SchoolTenantId);
        Assert.Equal(tenantId, admin.TenantId);

        var persisted = await db.SchoolAdministrators.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.SchoolAdminId == admin.SchoolAdminId);
        Assert.NotNull(persisted);
        Assert.Equal("admin@example.test", persisted!.InvitationEmail);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
