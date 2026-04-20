using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Notifications.Channels;

namespace Muallimi.Api.Notifications.ProductionProviderBindings;

/// <summary>
/// T055 (US2) — Email production provider binding.
///
/// Implements <see cref="INotificationChannelAdapter"/> for <c>email</c>.
/// Renders a minimal Arabic RTL HTML envelope around the body (Cairo font,
/// <c>dir="rtl"</c>, preserved diacritics) before posting to the configured
/// production endpoint. For local parity, the endpoint defaults to the Phase 4
/// email stub on <c>localhost:9402</c>; production configuration overrides
/// base URL via <c>NotificationProviderBinding.configuration</c>.
/// </summary>
public sealed class EmailProviderBinding : INotificationChannelAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger<EmailProviderBinding> _logger;

    public EmailProviderBinding(HttpClient http, ILogger<EmailProviderBinding> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Channel => "email";

    public async Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default)
    {
        var rendered = RenderHtml(request);
        var payload = new EmailPayload(
            Recipient: request.RecipientEmail ?? request.RecipientUserId.ToString("D"),
            Language: request.Language,
            Subject: request.Title,
            HtmlBody: rendered,
            CorrelationId: request.CorrelationId,
            TenantId: request.TenantId,
            NotificationKind: request.NotificationKind,
            Metadata: request.Metadata,
            BodyText: request.Body);

        var response = await _http.PostAsJsonAsync("/dispatch", payload, cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<NotificationDispatchReceipt>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Email provider returned empty receipt");
        _logger.LogInformation(
            "Email dispatched kind={Kind} correlation_id={CorrelationId} receipt_id={ReceiptId}",
            request.NotificationKind, request.CorrelationId, receipt.ReceiptId);
        return receipt;
    }

    private static string RenderHtml(NotificationDispatchRequest request)
    {
        var dir = string.Equals(request.Language, "ar", StringComparison.OrdinalIgnoreCase) ? "rtl" : "ltr";
        var lang = string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        // Keep the HTML envelope simple and UTF-8 so Arabic diacritics survive.
        return $"<!doctype html><html lang=\"{lang}\" dir=\"{dir}\">" +
               "<head><meta charset=\"utf-8\"/>" +
               "<style>body{font-family:Cairo,'Segoe UI',Tahoma,sans-serif;}</style>" +
               $"<title>{System.Net.WebUtility.HtmlEncode(request.Title)}</title></head>" +
               $"<body dir=\"{dir}\"><h1>{System.Net.WebUtility.HtmlEncode(request.Title)}</h1>" +
               $"<p>{System.Net.WebUtility.HtmlEncode(request.Body)}</p></body></html>";
    }

    private sealed record EmailPayload(
        [property: JsonPropertyName("recipient")] string Recipient,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html_body")] string HtmlBody,
        [property: JsonPropertyName("body_text")] string BodyText,
        [property: JsonPropertyName("correlation_id")] string CorrelationId,
        [property: JsonPropertyName("tenant_id")] Guid TenantId,
        [property: JsonPropertyName("notification_kind")] string NotificationKind,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string> Metadata);
}
