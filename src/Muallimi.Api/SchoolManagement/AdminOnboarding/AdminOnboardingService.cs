using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.SchoolManagement.AdminOnboarding;

/// <summary>
/// T037 (US1) — <c>AdminOnboardingService</c>.
///
/// Owns the invite / accept lifecycle for school administrators:
///   • <c>InviteAsync</c> — operator creates an invited admin row and
///     returns the invitation token. The <c>SchoolAdminId</c> doubles as
///     the opaque single-use invitation token (no separate token table).
///   • <c>CompleteOnboardingAsync</c> — invited admin posts back the token
///     together with their user identity + terms_accepted flag; the row
///     is promoted to <c>onboarded</c> and <c>terms_accepted_at</c> is set.
/// </summary>
public sealed record AdminInviteInput(
    Guid TenantId,
    Guid SchoolTenantId,
    string InvitationEmail,
    string DisplayNameAr,
    string DisplayNameEn);

public sealed record AdminCompleteInput(
    Guid InvitationToken,
    Guid UserIdentityId,
    bool TermsAccepted);

public interface IAdminOnboardingService
{
    Task<SchoolAdministrator> InviteAsync(AdminInviteInput input, CancellationToken ct = default);

    Task<SchoolAdministrator?> CompleteOnboardingAsync(AdminCompleteInput input, CancellationToken ct = default);
}

public sealed class AdminOnboardingService : IAdminOnboardingService
{
    private readonly ISchoolAdminRepository _repo;

    public AdminOnboardingService(ISchoolAdminRepository repo) => _repo = repo;

    public async Task<SchoolAdministrator> InviteAsync(AdminInviteInput input, CancellationToken ct = default)
    {
        var admin = new SchoolAdministrator
        {
            SchoolAdminId = Guid.NewGuid(),
            TenantId = input.TenantId,
            SchoolTenantId = input.SchoolTenantId,
            UserIdentityId = Guid.Empty,
            InvitationEmail = input.InvitationEmail,
            OnboardingStatus = "invited",
            CreatedAt = DateTime.UtcNow,
        };

        await _repo.AddAsync(admin, ct);
        await _repo.SaveChangesAsync(ct);
        return admin;
    }

    public async Task<SchoolAdministrator?> CompleteOnboardingAsync(
        AdminCompleteInput input,
        CancellationToken ct = default)
    {
        if (!input.TermsAccepted) return null;

        var admin = await _repo.GetByInvitationTokenAsync(input.InvitationToken, ct);
        if (admin is null) return null;
        if (admin.OnboardingStatus != "invited") return null;

        admin.UserIdentityId = input.UserIdentityId;
        admin.OnboardingStatus = "onboarded";
        admin.TermsAcceptedAt = DateTime.UtcNow;

        await _repo.SaveChangesAsync(ct);
        return admin;
    }
}

public static class AdminOnboardingServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5AdminOnboardingService(this IServiceCollection services)
    {
        services.AddScoped<IAdminOnboardingService, AdminOnboardingService>();
        return services;
    }
}
