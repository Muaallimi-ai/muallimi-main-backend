using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Parents.ParentNotifications;

/// <summary>
/// T015 — NotificationChannelAdapter pattern for Phase 4 parent notifications.
///
/// Three concrete adapters (<c>in_app</c>, <c>email</c>, <c>push</c>) implement
/// <see cref="INotificationChannelAdapter"/> against local stubs during
/// development. Production equivalents (transactional email provider, APNs/
/// FCM push provider) bind the same interface so no runtime path ever calls
/// a provider SDK directly — the constitution "provider abstraction" rule
/// applies.
///
/// The dispatcher (T129) picks the correct adapter via
/// <see cref="INotificationChannelAdapterRegistry"/> based on the parent's
/// channel preference and per-child override for the current notification
/// kind.
/// </summary>
public interface INotificationChannelAdapter
{
    string Channel { get; }
    Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default);
}

public sealed record NotificationDispatchRequest(
    Guid TenantId,
    Guid ParentProfileId,
    Guid ChildId,
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

public sealed class LocalStubNotificationChannelAdapter : INotificationChannelAdapter
{
    private readonly HttpClient _http;
    private readonly string _channel;

    public LocalStubNotificationChannelAdapter(HttpClient http, string channel)
    {
        _http = http;
        _channel = channel;
    }

    public string Channel => _channel;

    public async Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/dispatch", request, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<NotificationDispatchReceipt>(cancellationToken: ct);
        if (receipt is null)
        {
            throw new InvalidOperationException($"Stub dispatch returned empty receipt for channel {_channel}");
        }
        return receipt;
    }
}

public static class NotificationChannelAdapterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4NotificationChannelAdapters(this IServiceCollection services)
    {
        services.AddHttpClient("phase4-notification-in_app", c => c.BaseAddress = new Uri("http://localhost:9401"));
        services.AddHttpClient("phase4-notification-email", c => c.BaseAddress = new Uri("http://localhost:9402"));
        services.AddHttpClient("phase4-notification-push", c => c.BaseAddress = new Uri("http://localhost:9403"));

        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new LocalStubNotificationChannelAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("phase4-notification-in_app"),
                "in_app"));
        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new LocalStubNotificationChannelAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("phase4-notification-email"),
                "email"));
        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new LocalStubNotificationChannelAdapter(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("phase4-notification-push"),
                "push"));
        services.AddSingleton<INotificationChannelAdapterRegistry, NotificationChannelAdapterRegistry>();
        return services;
    }
}
