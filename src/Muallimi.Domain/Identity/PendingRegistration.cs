namespace Muallimi.Domain.Identity;

/// <summary>
/// Stores the parent's registration intent before payment is confirmed.
/// No User, Tenant, or ParentProfile exists until the payment webhook fires
/// and calls PaymentRegistrationService.CompleteFromPaymentAsync.
///
/// This is the correct SaaS pattern for mandatory-payment onboarding:
/// account data is only persisted once payment is confirmed.
/// </summary>
public class PendingRegistration
{
    public Guid Id { get; set; }

    /// <summary>Random token sent in the Paymob success redirect URL to verify the browser session.</summary>
    public string Nonce { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? FullNameEn { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Locale { get; set; } = "ar";

    /// <summary>Populated when the parent calls /payments/initiate (plan selected in step 2).</summary>
    public Guid PlanId { get; set; }

    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Short-lived token created by the payment webhook after account creation.
/// The browser success page exchanges this for a JWT using the original
/// pending_id + nonce (proof that this browser initiated the payment).
/// TTL: 15 minutes.
/// </summary>
public class PaymentSessionToken
{
    public Guid Id { get; set; }

    /// <summary>The original PendingRegistration.Id — used as the lookup key from the success page.</summary>
    public Guid PendingRegistrationId { get; set; }

    /// <summary>Same nonce as the PendingRegistration — verifies the browser that started the payment.</summary>
    public string Nonce { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    public bool Used { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
