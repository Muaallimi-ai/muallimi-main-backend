namespace Muallimi.Api.Compliance.AuditTrail;

/// <summary>
/// T115 + T121 — Locale-resolved human-readable labels for audit action types.
/// Keys align with the action_type strings emitted across the platform
/// (billing, impersonation, feature flags, incidents, payments, data rights,
/// content review). Unknown action types fall back to the raw key.
/// </summary>
public static class AuditActionLabels
{
    private static readonly Dictionary<string, (string Ar, string En)> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["subscription.created"] = ("تم إنشاء الاشتراك", "Subscription Created"),
        ["subscription.upgraded"] = ("ترقية الاشتراك", "Subscription Upgraded"),
        ["subscription.downgrade_scheduled"] = ("جُدوِلَ تخفيض الاشتراك", "Subscription Downgrade Scheduled"),
        ["subscription.cancelled"] = ("إلغاء الاشتراك", "Subscription Cancelled"),
        ["payment.success"] = ("نجاح الدفع", "Payment Success"),
        ["payment.failed"] = ("فشل الدفع", "Payment Failed"),
        ["payment.refunded"] = ("رد الدفع", "Payment Refunded"),
        ["payment_method.added"] = ("إضافة وسيلة دفع", "Payment Method Added"),
        ["payment_method.removed"] = ("إزالة وسيلة دفع", "Payment Method Removed"),
        ["operator.impersonation.started"] = ("بدء انتحال المُشغّل", "Operator Impersonation Started"),
        ["operator.impersonation.ended"] = ("انتهاء انتحال المُشغّل", "Operator Impersonation Ended"),
        ["operator.feature_flag.toggled"] = ("تبديل إعداد ميزة", "Feature Flag Toggled"),
        ["incident.created"] = ("فتح حادثة", "Incident Created"),
        ["incident.updated"] = ("تحديث حادثة", "Incident Updated"),
        ["incident.resolved"] = ("إغلاق حادثة", "Incident Resolved"),
        ["alert_rule.created"] = ("إنشاء قاعدة تنبيه", "Alert Rule Created"),
        ["alert_rule.modified"] = ("تعديل قاعدة تنبيه", "Alert Rule Modified"),
        ["alert_event.acknowledged"] = ("إقرار تنبيه", "Alert Event Acknowledged"),
        ["export_request"] = ("طلب تصدير بيانات", "Data Export Requested"),
        ["data_delete"] = ("حذف بيانات", "Data Deleted"),
        ["data_retention.executed"] = ("تنفيذ سياسة الاحتفاظ", "Retention Policy Executed"),
        ["data_retention.policy_updated"] = ("تعديل سياسة الاحتفاظ", "Retention Policy Updated"),
        ["audit_trail.exported"] = ("تصدير سجل التدقيق", "Audit Trail Exported"),
    };

    public static string Resolve(string actionType, string locale)
    {
        if (string.IsNullOrWhiteSpace(actionType)) return string.Empty;
        if (!Labels.TryGetValue(actionType, out var pair)) return actionType;
        return locale == "ar" ? pair.Ar : pair.En;
    }
}
