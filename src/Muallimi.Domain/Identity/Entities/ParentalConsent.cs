using System;
using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Per add-child redesign settled-decision #10: explicit parental
/// consent for the child's data. One row per (parent, child) pair.
/// Legacy children get backfilled with <c>IsLegacyAssumed = true</c>.
/// </summary>
public class ParentalConsent : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ParentUserId { get; set; }
    public Guid ChildUserId { get; set; }
    public DateTime ConsentedAt { get; set; }
    public string? IpAddress { get; set; }
    public bool IsLegacyAssumed { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
