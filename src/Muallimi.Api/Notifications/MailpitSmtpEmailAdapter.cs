using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Muallimi.Application.Notifications.Channels;

namespace Muallimi.Api.Notifications;

/// <summary>
/// Dev-only SMTP adapter that sends identity transactional emails (verification,
/// password reset, etc.) to Mailpit running in docker-compose on port 1025.
/// Browse captured emails at http://localhost:8025.
///
/// Registered only when <c>Mailpit:Enabled = true</c> in config (set in
/// appsettings.Development.json). In production the key is absent, so the
/// Phase 4 HTTP stub wins for the "email" channel instead.
/// </summary>
public sealed class MailpitSmtpEmailAdapter : INotificationChannelAdapter
{
    private readonly MailpitSmtpOptions _options;
    private readonly ILogger<MailpitSmtpEmailAdapter> _logger;

    public MailpitSmtpEmailAdapter(MailpitSmtpOptions options, ILogger<MailpitSmtpEmailAdapter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string Channel => "email";

    public async Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));

        var recipient = request.RecipientEmail ?? $"user-{request.RecipientUserId:D}@muallimi.local";
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = request.Title;

        var dir = string.Equals(request.Language, "ar", StringComparison.OrdinalIgnoreCase) ? "rtl" : "ltr";
        var lang = string.Equals(request.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        var title = System.Net.WebUtility.HtmlEncode(request.Title);
        var body = System.Net.WebUtility.HtmlEncode(request.Body);

        var html = $"<!doctype html><html lang=\"{lang}\" dir=\"{dir}\">" +
                   "<head><meta charset=\"utf-8\"/>" +
                   "<style>body{font-family:Cairo,'Segoe UI',Tahoma,sans-serif;padding:24px;}</style>" +
                   $"<title>{title}</title></head>" +
                   $"<body dir=\"{dir}\"><h2>{title}</h2><p>{body}</p></body></html>";

        var bodyBuilder = new BodyBuilder { HtmlBody = html, TextBody = request.Body };
        message.Body = bodyBuilder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.None, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);

        var receiptId = Guid.NewGuid().ToString("D");
        _logger.LogInformation(
            "Mailpit SMTP dispatched kind={Kind} to={Recipient} receipt_id={ReceiptId} correlation_id={CorrelationId}",
            request.NotificationKind, recipient, receiptId, request.CorrelationId);

        return new NotificationDispatchReceipt(receiptId, Channel);
    }
}

public sealed class MailpitSmtpOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 1025;
    public string FromAddress { get; init; } = "noreply@muallimi.local";
    public string FromName { get; init; } = "Muaallimi";
}
