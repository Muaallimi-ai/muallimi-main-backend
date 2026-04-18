using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.AdminOnboarding;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolTenant;

/// <summary>
/// T031 (US1) — Contract test for POST <c>/school-admin/onboarding/complete</c>.
///
/// Pins the endpoint route, request shape, and the state transition:
/// <c>invited</c> → <c>onboarded</c> with a non-null
/// <c>terms_accepted_at</c> timestamp. A single-use invariant is also
/// pinned — a second completion on an already-onboarded token returns
/// null (the endpoint surfaces 404).
/// </summary>
public class CompleteOnboardingTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/onboarding/complete", AdminOnboardingEndpoints.CompleteRoute);
    }

    [Fact]
    public void Request_Shape_Matches_Contract()
    {
        var props = PropertyNamesOf<AdminOnboardingEndpoints.CompleteOnboardingRequest>();
        Assert.Contains("invitation_token", props);
        Assert.Contains("user_identity_id", props);
        Assert.Contains("terms_accepted", props);
    }

    [Fact]
    public async Task Complete_Promotes_Invited_To_Onboarded()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolAdminRepository(db);
        var service = new AdminOnboardingService(repo);

        var admin = await service.InviteAsync(
            new AdminInviteInput(
                TenantId: Guid.NewGuid(),
                SchoolTenantId: Guid.NewGuid(),
                InvitationEmail: "admin@example.test",
                DisplayNameAr: "المدير",
                DisplayNameEn: "The Admin"),
            CancellationToken.None);

        var userId = Guid.NewGuid();
        var onboarded = await service.CompleteOnboardingAsync(
            new AdminCompleteInput(admin.SchoolAdminId, userId, TermsAccepted: true),
            CancellationToken.None);

        Assert.NotNull(onboarded);
        Assert.Equal("onboarded", onboarded!.OnboardingStatus);
        Assert.Equal(userId, onboarded.UserIdentityId);
        Assert.NotNull(onboarded.TermsAcceptedAt);
    }

    [Fact]
    public async Task Complete_Without_Terms_Accepted_Returns_Null()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolAdminRepository(db);
        var service = new AdminOnboardingService(repo);

        var admin = await service.InviteAsync(
            new AdminInviteInput(
                TenantId: Guid.NewGuid(),
                SchoolTenantId: Guid.NewGuid(),
                InvitationEmail: "admin@example.test",
                DisplayNameAr: "المدير",
                DisplayNameEn: "The Admin"),
            CancellationToken.None);

        var outcome = await service.CompleteOnboardingAsync(
            new AdminCompleteInput(admin.SchoolAdminId, Guid.NewGuid(), TermsAccepted: false),
            CancellationToken.None);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task Second_Completion_On_Same_Token_Returns_Null()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolAdminRepository(db);
        var service = new AdminOnboardingService(repo);

        var admin = await service.InviteAsync(
            new AdminInviteInput(
                TenantId: Guid.NewGuid(),
                SchoolTenantId: Guid.NewGuid(),
                InvitationEmail: "admin@example.test",
                DisplayNameAr: "المدير",
                DisplayNameEn: "The Admin"),
            CancellationToken.None);

        var first = await service.CompleteOnboardingAsync(
            new AdminCompleteInput(admin.SchoolAdminId, Guid.NewGuid(), TermsAccepted: true),
            CancellationToken.None);
        Assert.NotNull(first);

        var second = await service.CompleteOnboardingAsync(
            new AdminCompleteInput(admin.SchoolAdminId, Guid.NewGuid(), TermsAccepted: true),
            CancellationToken.None);
        Assert.Null(second);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
