using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Notifications.Channels;

/// <summary>
/// Phase 9 Part 2 — generalized platform-wide notification channel
/// abstraction. Previously lived under
/// <c>Muallimi.Api.Parents.ParentNotifications</c> with parent-specific
/// <c>ParentProfileId</c> + <c>ChildId</c> fields. Every concrete adapter
/// (<c>in_app</c>, <c>email</c>, <c>push</c>, <c>whatsapp</c>) implements
/// the same interface so dispatchers (parent, billing, announcement,
/// identity) never depend on provider SDKs directly — the constitution
/// "provider abstraction" rule applies unchanged.
///
/// Parent-specific context (<c>parent_profile_id</c>, <c>child_id</c>)
/// now travels in <see cref="NotificationDispatchRequest.Metadata"/>
/// so non-parent domains (identity transactional emails, billing
/// invoices, operator alerts) can reuse the same machinery without
/// inventing sentinel values.
/// </summary>
public interface INotificationChannelAdapter
{
    string Channel { get; }

    Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Generic notification dispatch request. Domain-specific fields go in
/// <see cref="Metadata"/> — e.g., parent flow puts <c>parent_profile_id</c>
/// and <c>child_id</c> there; billing puts <c>invoice_id</c>; identity
/// puts <c>reset_token_id</c>, <c>verification_token_id</c>, or
/// <c>session_id</c>.
/// </summary>
public sealed record NotificationDispatchRequest(
    Guid TenantId,
    Guid RecipientUserId,
    string? RecipientEmail,
    string NotificationKind,
    string Language,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Metadata,
    string CorrelationId);

public sealed record NotificationDispatchReceipt(
    [property: JsonPropertyName("receipt_id")] string ReceiptId,
    [property: JsonPropertyName("channel")] string Channel);

public interface INotificationChannelAdapterRegistry
{
    INotificationChannelAdapter Get(string channel);
}

public sealed class NotificationChannelAdapterRegistry : INotificationChannelAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, INotificationChannelAdapter> _byChannel;

    public NotificationChannelAdapterRegistry(IEnumerable<INotificationChannelAdapter> adapters)
    {
        var map = new Dictionary<string, INotificationChannelAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            map[adapter.Channel] = adapter;
        }
        _byChannel = map;
    }

    public INotificationChannelAdapter Get(string channel)
    {
        if (!_byChannel.TryGetValue(channel, out var adapter))
        {
            throw new InvalidOperationException($"No NotificationChannelAdapter registered for channel '{channel}'.");
        }
        return adapter;
    }
}
