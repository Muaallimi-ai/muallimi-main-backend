using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Parents.ParentNotifications;

namespace Muallimi.Api.Notifications.ProductionProviderBindings;

/// <summary>
/// T056 (US2) — Push production provider binding.
///
/// Implements <see cref="INotificationChannelAdapter"/> for <c>push</c>.
/// Builds a bilingual payload (title/body in both Arabic and English — the
/// device renders whichever matches the system locale) and posts to the
/// configured endpoint. For local parity the endpoint defaults to the Phase 4
/// push stub on <c>localhost:9403</c>.
/// </summary>
public sealed class PushProviderBinding : INotificationChannelAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger<PushProviderBinding> _logger;

    public PushProviderBinding(HttpClient http, ILogger<PushProviderBinding> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Channel => "push";

    public async Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default)
    {
        var isArabic = string.Equals(request.Language, "ar", StringComparison.OrdinalIgnoreCase);
        var payload = new PushPayload(
            Recipient: request.ParentProfileId.ToString("D"),
            TenantId: request.TenantId,
            NotificationKind: request.NotificationKind,
            CorrelationId: request.CorrelationId,
            PrimaryLanguage: isArabic ? "ar" : "en",
            TitleAr: isArabic ? request.Title : request.Title,
            BodyAr: isArabic ? request.Body : request.Body,
            TitleEn: request.Title,
            BodyEn: request.Body,
            Metadata: request.Metadata);

        var response = await _http.PostAsJsonAsync("/dispatch", payload, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<NotificationDispatchReceipt>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Push provider returned empty receipt");
        _logger.LogInformation(
            "Push dispatched kind={Kind} correlation_id={CorrelationId} receipt_id={ReceiptId}",
            request.NotificationKind, request.CorrelationId, receipt.ReceiptId);
        return receipt;
    }

    private sealed record PushPayload(
        [property: JsonPropertyName("recipient")] string Recipient,
        [property: JsonPropertyName("tenant_id")] Guid TenantId,
        [property: JsonPropertyName("notification_kind")] string NotificationKind,
        [property: JsonPropertyName("correlation_id")] string CorrelationId,
        [property: JsonPropertyName("primary_language")] string PrimaryLanguage,
        [property: JsonPropertyName("title_ar")] string TitleAr,
        [property: JsonPropertyName("body_ar")] string BodyAr,
        [property: JsonPropertyName("title_en")] string TitleEn,
        [property: JsonPropertyName("body_en")] string BodyEn,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string> Metadata);
}
