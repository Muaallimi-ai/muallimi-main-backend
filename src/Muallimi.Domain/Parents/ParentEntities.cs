using System;
using Muallimi.Domain.StudentExperience;

namespace Muallimi.Domain.Parents;

public class ParentProfile : ITenantScoped
{
    public Guid ParentProfileId { get; set; }
    public Guid TenantId { get; set; }
    public Guid IdentityId { get; set; }
    public string PreferredLanguage { get; set; } = "ar";
    public string Locale { get; set; } = "ar-SA";
    public string Timezone { get; set; } = "Asia/Dubai";
    public string NotificationChannels { get; set; } = "{\"in_app\":true,\"email\":true,\"push\":true}";
    public string QuietHours { get; set; } = "{}";
    public string PerChildOverrides { get; set; } = "{}";
    public string ConsentState { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ChildLink : ITenantScoped
{
    public Guid ChildLinkId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ParentProfileId { get; set; }
    public Guid StudentId { get; set; }
    public string Role { get; set; } = "guardian";
    public DateTime EffectiveStart { get; set; }
    public DateTime? EffectiveEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ParentNotification : ITenantScoped
{
    public Guid ParentNotificationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ParentProfileId { get; set; }
    public Guid ChildId { get; set; }
    public string NotificationKind { get; set; } = string.Empty;
    public string Channel { get; set; } = "in_app";
    public string Language { get; set; } = "ar";
    public string? BodyAr { get; set; }
    public string? BodyEn { get; set; }
    public DateTime? QuietHoursDeferredUntil { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string DeliveryState { get; set; } = "queued";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class OperatorImpersonationAudit : ITenantScoped
{
    public Guid OperatorImpersonationAuditId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OperatorActorId { get; set; }
    public Guid TargetParentProfileId { get; set; }
    public Guid? TargetChildId { get; set; }
    public string Surface { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime ViewedAt { get; set; }
}
