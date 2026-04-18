using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Parents.ParentNotifications;

namespace Muallimi.Api.Notifications.ProductionProviderBindings;

/// <summary>
/// T055–T057 (US2) — DI wiring for Phase 6 production provider bindings.
///
/// The Phase 4 local stub adapters (channels <c>in_app</c>, <c>email</c>,
/// <c>push</c>) stay registered untouched via
/// <c>AddPhase4LocalNotificationChannelStubs</c> so every existing parent
/// notification test keeps passing. Phase 6 adds <c>whatsapp</c> — a channel
/// Phase 4 did not ship — to the shared
/// <see cref="INotificationChannelAdapterRegistry"/>. Production-shaped email
/// and push bindings register as concrete types (<see cref="EmailProviderBinding"/>,
/// <see cref="PushProviderBinding"/>) so operators can swap them in once
/// production credentials are available, without disturbing the local loop.
/// </summary>
public static class Phase6ProductionProviderBindingRegistration
{
    public const string ClientEmail = "phase6-notification-email";
    public const string ClientPush = "phase6-notification-push";

    public static IServiceCollection AddPhase6ProductionProviderBindings(
        this IServiceCollection services,
        Phase6ProductionProviderBindingOptions? options = null)
    {
        options ??= Phase6ProductionProviderBindingOptions.LocalDefaults;

        services.AddHttpClient(ClientEmail, c => c.BaseAddress = new Uri(options.EmailBaseUrl));
        services.AddHttpClient(ClientPush, c => c.BaseAddress = new Uri(options.PushBaseUrl));

        services.AddSingleton<IWhatsAppProviderSink, LocalWhatsAppProviderSink>();

        // Concrete types for operator-pinned production wiring. Not added to
        // the Phase 4 adapter pool so local tests keep driving the stubs.
        services.AddSingleton(sp => new EmailProviderBinding(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClientEmail),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EmailProviderBinding>>()));
        services.AddSingleton(sp => new PushProviderBinding(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClientPush),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PushProviderBinding>>()));

        // WhatsApp is a net-new channel — add it to the shared registry so the
        // Phase 4 dispatcher and the Phase 6 delivery tracker can both resolve
        // it via INotificationChannelAdapterRegistry.Get("whatsapp").
        services.AddSingleton<INotificationChannelAdapter>(sp =>
            new WhatsAppProviderBinding(
                sp.GetRequiredService<IWhatsAppProviderSink>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WhatsAppProviderBinding>>()));

        return services;
    }
}

public sealed class Phase6ProductionProviderBindingOptions
{
    public string EmailBaseUrl { get; init; } = "http://localhost:9402";
    public string PushBaseUrl { get; init; } = "http://localhost:9403";

    public static Phase6ProductionProviderBindingOptions LocalDefaults { get; } = new();
}
