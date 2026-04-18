using Muallimi.Api.Compliance.AuditTrail;

namespace Muallimi.Api.Payments.PaymentProviderAdapter;

/// <summary>
/// T109 — Orchestrates saved payment method lifecycle through the swappable
/// IPaymentProviderAdapter. Every add/remove writes an audit trail entry so the
/// billing surface has a non-repudiable record of card management actions.
/// Listing is delegated directly to the adapter (no persistence of PAN data on
/// our side — only the provider holds the tokenised card).
/// </summary>
public interface IPaymentMethodManagementService
{
    Task<IReadOnlyList<PaymentMethod>> ListAsync(Guid tenantId, CancellationToken ct = default);
    Task<PaymentMethod> AddAsync(Guid tenantId, string type, string providerToken, string correlationId, CancellationToken ct = default);
    Task RemoveAsync(Guid tenantId, string paymentMethodRef, string correlationId, CancellationToken ct = default);
}

public sealed class PaymentMethodManagementService : IPaymentMethodManagementService
{
    private readonly IPaymentProviderAdapter _provider;
    private readonly AuditTrailWriter _audit;

    public PaymentMethodManagementService(IPaymentProviderAdapter provider, AuditTrailWriter audit)
    {
        _provider = provider;
        _audit = audit;
    }

    public Task<IReadOnlyList<PaymentMethod>> ListAsync(Guid tenantId, CancellationToken ct = default)
        => _provider.GetPaymentMethodsAsync(tenantId, ct);

    public async Task<PaymentMethod> AddAsync(Guid tenantId, string type, string providerToken, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerToken))
            throw new ArgumentException("providerToken must not be empty", nameof(providerToken));

        var method = await _provider.AddPaymentMethodAsync(
            new AddPaymentMethodRequest(tenantId, type, providerToken, correlationId), ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = tenantId,
            ActorId = tenantId,
            ActorType = "tenant",
            TargetType = "payment_method",
            ActionType = "payment_method.added",
            Payload = new { method_ref = method.Ref, type = method.Type, masked_identifier = method.MaskedIdentifier },
            CorrelationId = correlationId,
        }, ct);

        return method;
    }

    public async Task RemoveAsync(Guid tenantId, string paymentMethodRef, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(paymentMethodRef))
            throw new ArgumentException("paymentMethodRef must not be empty", nameof(paymentMethodRef));

        await _provider.RemovePaymentMethodAsync(tenantId, paymentMethodRef, ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = tenantId,
            ActorId = tenantId,
            ActorType = "tenant",
            TargetType = "payment_method",
            ActionType = "payment_method.removed",
            Payload = new { method_ref = paymentMethodRef },
            CorrelationId = correlationId,
        }, ct);
    }
}
