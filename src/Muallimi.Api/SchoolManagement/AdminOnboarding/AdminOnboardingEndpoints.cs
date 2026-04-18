using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.SchoolManagement.AdminOnboarding;

/// <summary>
/// T039 (US1) — Admin invite + onboarding endpoints.
///
///   • POST <c>/operator/schools/{school_tenant_id}/admins</c> (operator
///     context) — creates an invited SchoolAdministrator row.
///   • POST <c>/school-admin/onboarding/complete</c> (unauthenticated
///     token flow) — promotes an invited admin to onboarded after the user
///     presents the invitation token and accepts terms.
/// </summary>
public static class AdminOnboardingEndpoints
{
    public const string InviteRoute = "/api/operator/schools/{schoolTenantId:guid}/admins";
    public const string CompleteRoute = "/api/school-admin/onboarding/complete";

    public sealed record InviteAdminRequest(string email, string display_name_ar, string display_name_en);

    public sealed record CompleteOnboardingRequest(
        Guid invitation_token,
        Guid user_identity_id,
        bool terms_accepted);

    public static IEndpointRouteBuilder MapAdminOnboarding(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(InviteRoute, HandleInviteAsync)
            .WithName("InviteSchoolAdmin")
            .WithTags("SchoolManagement");
        routes.MapPost(CompleteRoute, HandleCompleteAsync)
            .WithName("CompleteSchoolAdminOnboarding")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleInviteAsync(
        Guid schoolTenantId,
        HttpContext http,
        InviteAdminRequest body,
        IAdminOnboardingService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetOperatorActorId(http, out _))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(body.email)
            || string.IsNullOrWhiteSpace(body.display_name_ar)
            || string.IsNullOrWhiteSpace(body.display_name_en))
        {
            return Results.BadRequest(new { error = "invalid_admin_invite_payload" });
        }

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var admin = await service.InviteAsync(
            new AdminInviteInput(
                TenantId: tenantId,
                SchoolTenantId: schoolTenantId,
                InvitationEmail: body.email,
                DisplayNameAr: body.display_name_ar,
                DisplayNameEn: body.display_name_en),
            ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Created(
            uri: $"/api/school-admin/admins/{admin.SchoolAdminId}",
            value: new
            {
                school_admin_id = admin.SchoolAdminId,
                onboarding_status = admin.OnboardingStatus,
                invitation_sent_at = admin.CreatedAt,
            });
    }

    public static async Task<IResult> HandleCompleteAsync(
        HttpContext http,
        CompleteOnboardingRequest body,
        IAdminOnboardingService service,
        CancellationToken ct)
    {
        if (body.invitation_token == Guid.Empty || body.user_identity_id == Guid.Empty || !body.terms_accepted)
        {
            return Results.BadRequest(new { error = "invalid_onboarding_request" });
        }

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var admin = await service.CompleteOnboardingAsync(
            new AdminCompleteInput(
                InvitationToken: body.invitation_token,
                UserIdentityId: body.user_identity_id,
                TermsAccepted: body.terms_accepted),
            ct);
        if (admin is null) return Results.NotFound(new { error = "invitation_not_found_or_consumed" });

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            school_admin_id = admin.SchoolAdminId,
            onboarding_status = admin.OnboardingStatus,
            school_tenant_id = admin.SchoolTenantId,
        });
    }
}
