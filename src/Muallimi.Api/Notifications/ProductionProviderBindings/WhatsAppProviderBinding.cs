using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Notifications.Channels;

namespace Muallimi.Api.Notifications.ProductionProviderBindings;

/// <summary>
/// T057 (US2) — WhatsApp production provider binding.
///
/// Implements <see cref="INotificationChannelAdapter"/> for <c>whatsapp</c>.
/// Preserves Arabic diacritics and emoji by passing the template body through
/// untouched (full Unicode). Uses an in-process local stub sink by default so
/// integration tests and the smoke script never need an external WhatsApp
/// Business API credential; production configuration swaps
/// <see cref="IWhatsAppProviderSink"/> for a real HTTP client.
/// </summary>
public interface IWhatsAppProviderSink
{
    Task<string> SendAsync(WhatsAppMessage message, CancellationToken ct);
    IReadOnlyCollection<WhatsAppMessage> DrainForInspection();
}

public sealed record WhatsAppMessage(
    Guid TenantId,
    Guid RecipientId,
    string NotificationKind,
    string Language,
    string Title,
    string Body,
    string CorrelationId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed class LocalWhatsAppProviderSink : IWhatsAppProviderSink
{
    private readonly ConcurrentQueue<WhatsAppMessage> _log = new();

    public Task<string> SendAsync(WhatsAppMessage message, CancellationToken ct)
    {
        // Byte-for-byte message retention lets tests assert diacritic fidelity.
        _log.Enqueue(message);
        return Task.FromResult($"wa-local-{Guid.NewGuid():N}");
    }

    public IReadOnlyCollection<WhatsAppMessage> DrainForInspection()
    {
        var copy = _log.ToArray();
        _log.Clear();
        return copy;
    }
}

public sealed class WhatsAppProviderBinding : INotificationChannelAdapter
{
    private readonly IWhatsAppProviderSink _sink;
    private readonly ILogger<WhatsAppProviderBinding> _logger;

    public WhatsAppProviderBinding(IWhatsAppProviderSink sink, ILogger<WhatsAppProviderBinding> logger)
    {
        _sink = sink;
        _logger = logger;
    }

    public string Channel => "whatsapp";

    public async Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default)
    {
        var providerMessageId = await _sink.SendAsync(new WhatsAppMessage(
            TenantId: request.TenantId,
            RecipientId: request.RecipientUserId,
            NotificationKind: request.NotificationKind,
            Language: request.Language,
            Title: request.Title,
            Body: request.Body,
            CorrelationId: request.CorrelationId,
            Metadata: request.Metadata), ct);

        _logger.LogInformation(
            "WhatsApp dispatched kind={Kind} correlation_id={CorrelationId} provider_message_id={Id}",
            request.NotificationKind, request.CorrelationId, providerMessageId);

        return new NotificationDispatchReceipt(providerMessageId, Channel);
    }
}
