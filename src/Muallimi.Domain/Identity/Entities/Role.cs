using System;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Role — named bundle of permissions with a <see cref="Scope"/>.
/// The 8 seeded roles are marked <c>IsSystem = true</c> and cannot be
/// renamed or deleted. Custom roles (future) live alongside with
/// <c>IsSystem = false</c>.
/// </summary>
public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RoleScope Scope { get; set; }
    public bool IsSystem { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
