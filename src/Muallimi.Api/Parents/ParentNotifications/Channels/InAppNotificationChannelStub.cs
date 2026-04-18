using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Parents.ParentNotifications.Channels;

/// <summary>
/// T130 (US7) — Local in-app <see cref="INotificationChannelAdapter"/> stub.
///
/// Writes delivery receipts to an in-memory log keyed by
/// <c>parent_notification_id</c> so integration tests can assert round-trips
/// without external infrastructure. A production implementation binds the
/// same interface behind a web-socket / long-poll pipe — the dispatcher
/// above never sees the difference (constitution "provider abstraction"
/// rule).
/// </summary>
public interface INotificationChannelStubLedger
{
    IReadOnlyList<NotificationDispatchStubReceipt> ReceiptsFor(Guid parentNotificationId);
    void Record(NotificationDispatchStubReceipt receipt);
    void Clear();
}

public sealed record NotificationDispatchStubReceipt(
    Guid ParentNotificationId,
    Guid TenantId,
    Guid ParentProfileId,
    Guid ChildId,
    string Channel,
    string Language,
    string Body,
    string DeepLink,
    string ReceiptId,
    DateTime DispatchedAt,
    string CorrelationId);

public sealed class NotificationChannelStubLedger : INotificationChannelStubLedger
{
    private readonly ConcurrentDictionary<Guid, List<NotificationDispatchStubReceipt>> _receipts = new();

    public void Record(NotificationDispatchStubReceipt receipt)
    {
        var list = _receipts.GetOrAdd(receipt.ParentNotificationId, _ => new List<NotificationDispatchStubReceipt>());
        lock (list) list.Add(receipt);
    }

    public IReadOnlyList<NotificationDispatchStubReceipt> ReceiptsFor(Guid parentNotificationId)
    {
        if (!_receipts.TryGetValue(parentNotificationId, out var list)) return Array.Empty<NotificationDispatchStubReceipt>();
        lock (list) return list.ToArray();
    }

    public void Clear() => _receipts.Clear();
}

internal abstract class LocalStubChannelAdapter : INotificationChannelAdapter
{
    private readonly INotificationChannelStubLedger _ledger;

    protected LocalStubChannelAdapter(INotificationChannelStubLedger ledger)
    {
        _ledger = ledger;
    }

    public abstract string Channel { get; }

    public Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default)
    {
        var receiptId = Guid.NewGuid().ToString("D");
        var dispatchedAt = DateTime.UtcNow;
        request.Metadata.TryGetValue("parent_notification_id", out var nidRaw);
        request.Metadata.TryGetValue("deep_link", out var deepLink);
        Guid.TryParse(nidRaw, out var parentNotificationId);

        _ledger.Record(new NotificationDispatchStubReceipt(
            ParentNotificationId: parentNotificationId,
            TenantId: request.TenantId,
            ParentProfileId: request.ParentProfileId,
            ChildId: request.ChildId,
            Channel: Channel,
            Language: request.Language,
            Body: request.Body,
            DeepLink: deepLink ?? string.Empty,
            ReceiptId: receiptId,
            DispatchedAt: dispatchedAt,
            CorrelationId: request.CorrelationId));

        return Task.FromResult(new NotificationDispatchReceipt(receiptId, Channel));
    }
}

internal sealed class InAppNotificationChannelStub : LocalStubChannelAdapter
{
    public InAppNotificationChannelStub(INotificationChannelStubLedger ledger) : base(ledger) { }
    public override string Channel => "in_app";
}

internal sealed class EmailNotificationChannelStub : LocalStubChannelAdapter
{
    public EmailNotificationChannelStub(INotificationChannelStubLedger ledger) : base(ledger) { }
    public override string Channel => "email";
}

internal sealed class PushNotificationChannelStub : LocalStubChannelAdapter
{
    public PushNotificationChannelStub(INotificationChannelStubLedger ledger) : base(ledger) { }
    public override string Channel => "push";
}

public static class NotificationChannelStubServiceCollectionExtensions
{
    /// <summary>
    /// Registers the three local-stub channel adapters behind
    /// <see cref="INotificationChannelAdapter"/> and the shared
    /// <see cref="INotificationChannelStubLedger"/>. Safe to call from both
    /// runtime bootstrap (local parity) and integration tests.
    /// </summary>
    public static IServiceCollection AddPhase4LocalNotificationChannelStubs(this IServiceCollection services)
    {
        services.AddSingleton<INotificationChannelStubLedger, NotificationChannelStubLedger>();
        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new InAppNotificationChannelStub(sp.GetRequiredService<INotificationChannelStubLedger>()));
        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new EmailNotificationChannelStub(sp.GetRequiredService<INotificationChannelStubLedger>()));
        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new PushNotificationChannelStub(sp.GetRequiredService<INotificationChannelStubLedger>()));
        services.AddSingleton<INotificationChannelAdapterRegistry, NotificationChannelAdapterRegistry>();
        return services;
    }
}
