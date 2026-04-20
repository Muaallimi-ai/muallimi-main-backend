using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Notifications.Channels;

namespace Muallimi.Application.Identity.Notifications;

/// <summary>
/// T037 — Identity-scoped notification sender. Thin wrapper over the
/// generalized <see cref="INotificationChannelAdapterRegistry"/>:
/// resolve the template, build a <see cref="NotificationDispatchRequest"/>,
/// dispatch. Identity emails are transactional — no quiet hours, no
/// per-user channel preferences, no deferral. If delivery fails, the
/// error bubbles up for the caller to map to an audit event.
///
/// Channel is fixed per template kind: email verification + password
/// reset + password changed + unusual login go to <c>email</c>;
/// invitation also goes to <c>email</c>; child-created goes to
/// <c>in_app</c> (the parent sees it inside the platform the moment they
/// create the child — email is a convenience, not the source of truth).
/// </summary>
public interface IIdentityNotificationSender
{
    Task<NotificationDispatchReceipt> SendEmailVerificationAsync(
        IdentityNotificationRecipient recipient,
        string verificationLink,
        string correlationId,
        CancellationToken ct = default);

    Task<NotificationDispatchReceipt> SendPasswordResetAsync(
        IdentityNotificationRecipient recipient,
        string resetLink,
        string correlationId,
        CancellationToken ct = default);

    Task<NotificationDispatchReceipt> SendPasswordChangedAsync(
        IdentityNotificationRecipient recipient,
        string correlationId,
        CancellationToken ct = default);

    Task<NotificationDispatchReceipt> SendInvitationAsync(
        IdentityNotificationRecipient recipient,
        string role,
        string invitationLink,
        string correlationId,
        CancellationToken ct = default);

    Task<NotificationDispatchReceipt> SendUnusualLoginAsync(
        IdentityNotificationRecipient recipient,
        string device,
        string location,
        string correlationId,
        CancellationToken ct = default);

    Task<NotificationDispatchReceipt> SendChildCreatedAsync(
        IdentityNotificationRecipient recipient,
        string childName,
        string username,
        string tempPassword,
        string correlationId,
        CancellationToken ct = default);

    /// <summary>US5 — notify the managing parent of a child's unusual login.</summary>
    Task<NotificationDispatchReceipt> SendChildUnusualLoginAsync(
        IdentityNotificationRecipient parentRecipient,
        string childName,
        string device,
        string location,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record IdentityNotificationRecipient(
    Guid TenantId,
    Guid UserId,
    string? Email,
    string FullName,
    string Locale);

public sealed class IdentityNotificationSender : IIdentityNotificationSender
{
    private readonly INotificationChannelAdapterRegistry _channels;
    private readonly ILogger<IdentityNotificationSender> _logger;

    public IdentityNotificationSender(
        INotificationChannelAdapterRegistry channels,
        ILogger<IdentityNotificationSender> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public Task<NotificationDispatchReceipt> SendEmailVerificationAsync(
        IdentityNotificationRecipient recipient,
        string verificationLink,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "email",
            templateKey: IdentityTemplateKeys.EmailVerification,
            recipient: recipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = recipient.FullName,
                ["verification_link"] = verificationLink,
            },
            ct: ct);

    public Task<NotificationDispatchReceipt> SendPasswordResetAsync(
        IdentityNotificationRecipient recipient,
        string resetLink,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "email",
            templateKey: IdentityTemplateKeys.PasswordReset,
            recipient: recipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = recipient.FullName,
                ["reset_link"] = resetLink,
            },
            ct: ct);

    public Task<NotificationDispatchReceipt> SendPasswordChangedAsync(
        IdentityNotificationRecipient recipient,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "email",
            templateKey: IdentityTemplateKeys.PasswordChanged,
            recipient: recipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = recipient.FullName,
            },
            ct: ct);

    public Task<NotificationDispatchReceipt> SendInvitationAsync(
        IdentityNotificationRecipient recipient,
        string role,
        string invitationLink,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "email",
            templateKey: IdentityTemplateKeys.Invitation,
            recipient: recipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = recipient.FullName,
                ["role"] = role,
                ["invitation_link"] = invitationLink,
            },
            ct: ct);

    public Task<NotificationDispatchReceipt> SendUnusualLoginAsync(
        IdentityNotificationRecipient recipient,
        string device,
        string location,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "email",
            templateKey: IdentityTemplateKeys.UnusualLogin,
            recipient: recipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = recipient.FullName,
                ["device"] = device,
                ["location"] = location,
            },
            ct: ct);

    public Task<NotificationDispatchReceipt> SendChildCreatedAsync(
        IdentityNotificationRecipient recipient,
        string childName,
        string username,
        string tempPassword,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "in_app",
            templateKey: IdentityTemplateKeys.ChildCreated,
            recipient: recipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = recipient.FullName,
                ["child_name"] = childName,
                ["username"] = username,
                ["temp_password"] = tempPassword,
            },
            ct: ct);

    public Task<NotificationDispatchReceipt> SendChildUnusualLoginAsync(
        IdentityNotificationRecipient parentRecipient,
        string childName,
        string device,
        string location,
        string correlationId,
        CancellationToken ct = default)
        => SendAsync(
            channel: "in_app",
            templateKey: IdentityTemplateKeys.ChildUnusualLogin,
            recipient: parentRecipient,
            correlationId: correlationId,
            variables: new Dictionary<string, string>
            {
                ["full_name"] = parentRecipient.FullName,
                ["child_name"] = childName,
                ["device"] = device,
                ["location"] = location,
            },
            ct: ct);

    private async Task<NotificationDispatchReceipt> SendAsync(
        string channel,
        string templateKey,
        IdentityNotificationRecipient recipient,
        string correlationId,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken ct)
    {
        var rendering = IdentityEmailTemplates.Render(templateKey, recipient.Locale, variables);
        var adapter = _channels.Get(channel);

        var metadata = new Dictionary<string, string>(variables)
        {
            ["identity_user_id"] = recipient.UserId.ToString("D"),
            ["template_key"] = templateKey,
        };

        var request = new NotificationDispatchRequest(
            TenantId: recipient.TenantId,
            RecipientUserId: recipient.UserId,
            RecipientEmail: recipient.Email,
            NotificationKind: templateKey,
            Language: recipient.Locale,
            Title: rendering.Subject,
            Body: rendering.Body,
            Metadata: metadata,
            CorrelationId: correlationId);

        var receipt = await adapter.DispatchAsync(request, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Identity notification dispatched template={Template} channel={Channel} receipt_id={ReceiptId} correlation_id={CorrelationId}",
            templateKey, channel, receipt.ReceiptId, correlationId);
        return receipt;
    }
}
