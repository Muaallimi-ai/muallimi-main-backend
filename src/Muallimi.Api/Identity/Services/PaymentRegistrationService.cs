using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// Completes the payment-gated registration flow.
///
/// The correct SaaS flow for mandatory-payment onboarding:
///   1. RegisterParentAsync     → creates PendingRegistration only (no User/Tenant in DB).
///   2. PaymentInitiateEndpoint → builds Paymob order, merchant_order_id = PendingRegistration.Id.
///   3. Parent pays on Paymob hosted page.
///   4. Paymob webhook fires    → CompleteFromPaymentAsync creates User+Tenant+ParentProfile+Subscription atomically.
///   5. PaymentSessionToken     → success page exchanges (pending_id + nonce) for the JWT.
///
/// No orphan accounts. If payment is abandoned the PendingRegistration expires after 1 hour
/// and is cleaned up by the background job (or replaced silently on next registration attempt).
/// </summary>
public interface IPaymentRegistrationService
{
    /// <summary>
    /// Validates the pending registration and updates it with the chosen plan.
    /// Called by the payment initiate endpoint before creating the Paymob order.
    /// Returns null if the pending registration is not found or expired.
    /// </summary>
    Task<PendingRegistration?> PrepareForPaymentAsync(Guid pendingId, string nonce, Guid planId, CancellationToken ct = default);

    /// <summary>
    /// Called by the payment webhook on success.
    /// Creates User + Tenant + ParentProfile + Subscription atomically,
    /// issues a JWT, and stores a short-lived PaymentSessionToken for the success page.
    /// </summary>
    Task CompleteFromPaymentAsync(Guid pendingId, string providerReference, string correlationId, CancellationToken ct = default);

    /// <summary>
    /// Called by the success page after Paymob redirects the browser back.
    /// Returns (accessToken, refreshToken) once the webhook has completed,
    /// or null while the webhook is still in-flight (caller should retry).
    /// The token is single-use and expires after 15 minutes.
    /// </summary>
    Task<(string AccessToken, string RefreshToken)?> ExchangeSessionTokenAsync(Guid pendingId, string nonce, CancellationToken ct = default);
}

public sealed class PaymentRegistrationService : IPaymentRegistrationService
{
    private readonly MuallimiDbContext _db;
    private readonly IAuthService _auth;
    private readonly ITokenService _tokens;
    private readonly ISessionService _sessions;
    private readonly IProfileIdsResolver _profileIds;

    public PaymentRegistrationService(
        MuallimiDbContext db,
        IAuthService auth,
        ITokenService tokens,
        ISessionService sessions,
        IProfileIdsResolver profileIds)
    {
        _db = db;
        _auth = auth;
        _tokens = tokens;
        _sessions = sessions;
        _profileIds = profileIds;
    }

    public async Task<PendingRegistration?> PrepareForPaymentAsync(Guid pendingId, string nonce, Guid planId, CancellationToken ct = default)
    {
        var pending = await _db.PendingRegistrations
            .FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);

        if (pending is null || pending.Nonce != nonce || pending.ExpiresAt < DateTime.UtcNow)
            return null;

        pending.PlanId = planId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return pending;
    }

    public async Task CompleteFromPaymentAsync(Guid pendingId, string providerReference, string correlationId, CancellationToken ct = default)
    {
        var pending = await _db.PendingRegistrations
            .FirstOrDefaultAsync(p => p.Id == pendingId, ct).ConfigureAwait(false);

        if (pending is null) return;

        // Idempotency guard — if a PaymentSessionToken already exists for this pending_id,
        // the webhook has already been processed (e.g. duplicate delivery).
        var alreadyDone = await _db.PaymentSessionTokens
            .AnyAsync(t => t.PendingRegistrationId == pendingId, ct).ConfigureAwait(false);
        if (alreadyDone) return;

        var plan = await _db.SubscriptionPlans.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PlanId == pending.PlanId && p.IsActive, ct).ConfigureAwait(false);
        if (plan is null) return;

        var parentRole = await _db.IdentityRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == "parent", ct).ConfigureAwait(false);
        if (parentRole is null) return;

        // ── Create User + Tenant + ParentProfile + Subscription atomically ──

        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Type = TenantType.Family,
            DisplayName = pending.FullName,
            Locale = pending.Locale,
            Status = TenantStatus.Active,
            Metadata = "{}",
            CreatedAt = now,
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            AccountType = AccountType.Personal,
            Email = pending.Email,
            NormalizedEmail = pending.NormalizedEmail,
            FullName = pending.FullName,
            FullNameEn = pending.FullNameEn,
            Locale = pending.Locale,
            Status = UserStatus.Active,
            PasswordHash = pending.PasswordHash,
            PasswordChangedAt = now,
            PhoneNumber = pending.PhoneNumber,
            CreatedAt = now,
        };

        var grant = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = parentRole.Id,
            TenantId = tenant.Id,
            GrantedBy = user.Id,
            GrantedAt = now,
        };

        var parentProfile = new ParentProfile
        {
            ParentProfileId = Guid.NewGuid(),
            TenantId = tenant.Id,
            IdentityId = user.Id,
            UserId = user.Id,
            PreferredLanguage = pending.Locale == "en" ? "en" : "ar",
            Locale = pending.Locale == "en" ? "en-US" : "ar-EG",
            Timezone = "Africa/Cairo",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var cycleEnd = plan.BillingCycle == "yearly" ? now.AddYears(1) : now.AddMonths(1);
        var subscription = new Subscription
        {
            SubscriptionId = Guid.NewGuid(),
            TenantId = tenant.Id,
            PlanId = plan.PlanId,
            PlanType = plan.PlanType,
            Status = "active",
            CurrentPeriodStart = now,
            CurrentPeriodEnd = cycleEnd,
            PaymentMethodRef = providerReference,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.IdentityTenants.Add(tenant);
        _db.IdentityUsers.Add(user);
        _db.IdentityUserRoles.Add(grant);
        _db.ParentProfiles.Add(parentProfile);
        _db.Subscriptions.Add(subscription);

        // ── Issue JWT so the success page can log the user in immediately ──

        var session = await _sessions.CreateAsync(new CreateSessionInput(
            UserId: user.Id,
            IpAddress: pending.IpAddress,
            UserAgent: pending.UserAgent,
            DeviceName: "registration",
            DeviceType: Muallimi.Domain.Identity.Enums.DeviceType.Unknown), ct).ConfigureAwait(false);

        var profileIdMap = await _profileIds.ResolveAsync(user.Id, tenant.Id, ct).ConfigureAwait(false);
        var access  = _tokens.GenerateAccessToken(user, tenant.Type, new[] { "parent" }, session.Id, profileIds: profileIdMap);
        var refresh = _tokens.GenerateRefreshToken();

        _db.IdentityRefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            SessionId = session.Id,
            TokenHash = refresh.hash,
            IssuedAt = now,
            ExpiresAt = now.AddDays(7),
            CreatedByIp = pending.IpAddress,
        });

        var sessionToken = new PaymentSessionToken
        {
            Id = Guid.NewGuid(),
            PendingRegistrationId = pendingId,
            Nonce = pending.Nonce,
            AccessToken  = access.Token,
            RefreshToken = refresh.token,
            Used = false,
            ExpiresAt = now.AddMinutes(15),
            CreatedAt = now,
        };
        _db.PaymentSessionTokens.Add(sessionToken);

        // ── Remove the pending registration (account now fully created) ──
        _db.PendingRegistrations.Remove(pending);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Send email verification link now that the account is active.
        await _auth.ActivateUserForTenantAsync(tenant.Id, correlationId, ct).ConfigureAwait(false);
    }

    public async Task<(string AccessToken, string RefreshToken)?> ExchangeSessionTokenAsync(
        Guid pendingId, string nonce, CancellationToken ct = default)
    {
        var token = await _db.PaymentSessionTokens
            .FirstOrDefaultAsync(t =>
                t.PendingRegistrationId == pendingId &&
                t.Nonce == nonce &&
                !t.Used &&
                t.ExpiresAt > DateTime.UtcNow, ct).ConfigureAwait(false);

        if (token is null) return null;

        token.Used = true;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (token.AccessToken, token.RefreshToken);
    }
}
