namespace Muallimi.Infrastructure.Identity.EfCore;

/// <summary>
/// T025 — marker / documentation for the Phase 9 Identity tenant query
/// filter wiring. The actual filter calls live in
/// <c>MuallimiDbContext.ApplyPhase9IdentityTenantFilters</c> because the
/// generic filter helper (<c>ApplyTenantFilter&lt;T&gt;</c>) is a private
/// method on the DbContext and consistent with Phases 1-6 (one
/// <c>ApplyPhaseN...Filters</c> method per module, all co-located on
/// the DbContext).
///
/// Scoped entities (Phase 9):
///   - <see cref="Muallimi.Domain.Identity.Entities.User"/>
///   - <see cref="Muallimi.Domain.Identity.Entities.UserRole"/>
///
/// Transitively filtered (scoped via their owning User's TenantId, not
/// directly ITenantScoped — enforced by the
/// <c>CrossTenantIsolationTests</c> added in T060):
///   - <see cref="Muallimi.Domain.Identity.Entities.RefreshToken"/>
///   - <see cref="Muallimi.Domain.Identity.Entities.UserSession"/>
///   - <see cref="Muallimi.Domain.Identity.Entities.EmailVerificationToken"/>
///   - <see cref="Muallimi.Domain.Identity.Entities.PasswordResetToken"/>
///   - <see cref="Muallimi.Domain.Identity.Entities.TwoFactorSecret"/>
///
/// Super-admin cross-tenant bypass: use <c>IgnoreQueryFilters()</c> at
/// the repository call-site and emit an audit event. See
/// <c>IUserManagementService</c> operator paths (T034 in US3).
/// </summary>
public static class IdentityTenantQueryFilterMarker
{
}
