namespace Muallimi.Api.Payments.PaymentProviderAdapter;

/// <summary>
/// Registry that holds all registered payment provider adapters and resolves the
/// correct one by name. Currently Paymob is the only registered provider; adding a
/// second provider (e.g. Fawry, Stripe) requires only:
///   1. A new class implementing IPaymentProviderAdapter.
///   2. A new IWebhookSignatureValidator implementation.
///   3. Two DI registrations in Program.cs — no other code changes.
/// </summary>
public interface IPaymentProviderAdapterRegistry
{
    /// <summary>Returns the platform-default adapter (first registered).</summary>
    IPaymentProviderAdapter GetDefault();

    /// <summary>Returns the adapter for the given provider name, or null if not registered.</summary>
    IPaymentProviderAdapter? Resolve(string providerName);

    /// <summary>Names of all registered providers, in registration order.</summary>
    IReadOnlyList<string> AvailableProviders { get; }
}

public sealed class PaymentProviderAdapterRegistry : IPaymentProviderAdapterRegistry
{
    private readonly IReadOnlyList<IPaymentProviderAdapter> _adapters;
    private readonly IReadOnlyDictionary<string, IPaymentProviderAdapter> _byName;

    public PaymentProviderAdapterRegistry(IEnumerable<IPaymentProviderAdapter> adapters)
    {
        _adapters = adapters.ToList();
        _byName = _adapters.ToDictionary(a => a.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IPaymentProviderAdapter GetDefault()
    {
        if (_adapters.Count == 0)
            throw new InvalidOperationException("No payment provider adapters are registered.");
        return _adapters[0];
    }

    public IPaymentProviderAdapter? Resolve(string providerName)
        => _byName.TryGetValue(providerName, out var adapter) ? adapter : null;

    public IReadOnlyList<string> AvailableProviders
        => _adapters.Select(a => a.ProviderName).ToList();
}
