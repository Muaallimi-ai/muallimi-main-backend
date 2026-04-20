using System;
using System.Collections.Generic;

namespace Muallimi.Application.Identity.Notifications;

/// <summary>
/// T036 — Phase 9 identity notification template registry. Six template
/// keys, each with an Arabic (primary) and English variant. Rendering is
/// a small placeholder-substitution pass — the identity domain is the
/// only consumer, so pulling in a full templating engine would be
/// overkill. Extract a platform-wide <c>INotificationTemplateRenderer</c>
/// when a second domain (billing, school onboarding) needs similar
/// rendering.
///
/// Placeholders use <c>{var_name}</c> syntax. Missing variables render
/// as empty string.
/// </summary>
public static class IdentityTemplateKeys
{
    public const string EmailVerification = "identity.email_verification";
    public const string PasswordReset = "identity.password_reset";
    public const string PasswordChanged = "identity.password_changed";
    public const string Invitation = "identity.invitation";
    public const string UnusualLogin = "identity.unusual_login";
    public const string ChildCreated = "identity.child_created";
    public const string ChildUnusualLogin = "identity.child_unusual_login";
}

public sealed record IdentityEmailRendering(string Subject, string Body);

public static class IdentityEmailTemplates
{
    private static readonly IReadOnlyDictionary<string, IdentityEmailRendering> ArabicTemplates = new Dictionary<string, IdentityEmailRendering>
    {
        [IdentityTemplateKeys.EmailVerification] = new(
            Subject: "تأكيد البريد الإلكتروني — معلّمي",
            Body: "مرحبًا {full_name},\nالرجاء تأكيد بريدك الإلكتروني عبر الرابط التالي:\n{verification_link}\nالرابط صالح لمدة 24 ساعة."),

        [IdentityTemplateKeys.PasswordReset] = new(
            Subject: "إعادة تعيين كلمة المرور — معلّمي",
            Body: "مرحبًا {full_name},\nاستلمنا طلبًا لإعادة تعيين كلمة المرور الخاصة بك.\nأكمل العملية خلال ساعة واحدة عبر:\n{reset_link}\nإن لم تطلب ذلك تجاهل هذه الرسالة."),

        [IdentityTemplateKeys.PasswordChanged] = new(
            Subject: "تم تغيير كلمة المرور — معلّمي",
            Body: "مرحبًا {full_name},\nتم تغيير كلمة المرور الخاصة بحسابك للتو.\nإن لم تقم بذلك فاتصل بالدعم فورًا."),

        [IdentityTemplateKeys.Invitation] = new(
            Subject: "دعوة للانضمام إلى معلّمي",
            Body: "مرحبًا {full_name},\nتمت دعوتك للانضمام إلى منصة معلّمي بالدور: {role}.\nأكمل التسجيل وتعيين كلمة المرور عبر:\n{invitation_link}"),

        [IdentityTemplateKeys.UnusualLogin] = new(
            Subject: "تنبيه أمني — دخول غير معتاد إلى حسابك",
            Body: "مرحبًا {full_name},\nلاحظنا تسجيل دخول من جهاز جديد أو موقع غير معتاد:\nالجهاز: {device}\nالموقع: {location}\nإن لم يكن ذلك أنت غيّر كلمة المرور فورًا من صفحة الإعدادات."),

        [IdentityTemplateKeys.ChildCreated] = new(
            Subject: "تم إنشاء حساب ابنك/ابنتك — معلّمي",
            Body: "مرحبًا {full_name},\nتم إنشاء حساب تعليمي لطفلك: {child_name}.\nاسم المستخدم: {username}\nكلمة المرور المؤقتة: {temp_password}\nبإمكانك تغيير كلمة المرور في أي وقت من صفحة \"أطفالي\"."),

        [IdentityTemplateKeys.ChildUnusualLogin] = new(
            Subject: "تنبيه أمني — دخول غير معتاد على حساب طفلك",
            Body: "مرحبًا {full_name},\nلاحظنا تسجيل دخول لطفلك {child_name} من جهاز جديد أو موقع غير معتاد:\nالجهاز: {device}\nالموقع: {location}\nإن لم يكن ذلك بإذنك، بإمكانك إنهاء جلسته من صفحة «أطفالي»."),
    };

    private static readonly IReadOnlyDictionary<string, IdentityEmailRendering> EnglishTemplates = new Dictionary<string, IdentityEmailRendering>
    {
        [IdentityTemplateKeys.EmailVerification] = new(
            Subject: "Confirm your email — Muaallimi",
            Body: "Hi {full_name},\nPlease confirm your email via the link below:\n{verification_link}\nThis link is valid for 24 hours."),

        [IdentityTemplateKeys.PasswordReset] = new(
            Subject: "Password reset — Muaallimi",
            Body: "Hi {full_name},\nWe received a request to reset your password.\nComplete the reset within one hour via:\n{reset_link}\nIf you did not request this, you can ignore this message."),

        [IdentityTemplateKeys.PasswordChanged] = new(
            Subject: "Your password was changed — Muaallimi",
            Body: "Hi {full_name},\nYour account password was just changed.\nIf this was not you, contact support immediately."),

        [IdentityTemplateKeys.Invitation] = new(
            Subject: "You're invited to Muaallimi",
            Body: "Hi {full_name},\nYou've been invited to join Muaallimi as: {role}.\nComplete registration and set your password at:\n{invitation_link}"),

        [IdentityTemplateKeys.UnusualLogin] = new(
            Subject: "Security alert — unusual sign-in detected",
            Body: "Hi {full_name},\nWe detected a sign-in from a new device or unfamiliar location:\nDevice: {device}\nLocation: {location}\nIf this was not you, change your password immediately from Settings."),

        [IdentityTemplateKeys.ChildCreated] = new(
            Subject: "Your child's account was created — Muaallimi",
            Body: "Hi {full_name},\nA learner account was created for your child: {child_name}.\nUsername: {username}\nTemporary password: {temp_password}\nYou can change the password at any time from the \"My Children\" page."),

        [IdentityTemplateKeys.ChildUnusualLogin] = new(
            Subject: "Security alert — unusual sign-in on your child's account",
            Body: "Hi {full_name},\nWe detected a sign-in for your child {child_name} from a new device or unfamiliar location:\nDevice: {device}\nLocation: {location}\nIf this was not authorised, you can end their session from the \"My Children\" page."),
    };

    public static IdentityEmailRendering Render(
        string templateKey,
        string locale,
        IReadOnlyDictionary<string, string> variables)
    {
        var map = string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? EnglishTemplates : ArabicTemplates;
        if (!map.TryGetValue(templateKey, out var template))
        {
            throw new ArgumentException($"Unknown identity template key '{templateKey}'.", nameof(templateKey));
        }
        return new IdentityEmailRendering(
            Subject: Substitute(template.Subject, variables),
            Body: Substitute(template.Body, variables));
    }

    private static string Substitute(string source, IReadOnlyDictionary<string, string> variables)
    {
        if (variables.Count == 0) return source;
        var buffer = source;
        foreach (var (name, value) in variables)
        {
            buffer = buffer.Replace("{" + name + "}", value ?? string.Empty, StringComparison.Ordinal);
        }
        // Strip any remaining unresolved placeholders to avoid leaking the
        // bare "{var}" form to the user.
        var sb = new System.Text.StringBuilder(buffer.Length);
        var i = 0;
        while (i < buffer.Length)
        {
            if (buffer[i] == '{')
            {
                var close = buffer.IndexOf('}', i + 1);
                if (close > i)
                {
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(buffer[i]);
            i++;
        }
        return sb.ToString();
    }
}
