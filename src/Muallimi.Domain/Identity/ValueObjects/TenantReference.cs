using System;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity.ValueObjects;

/// <summary>
/// Lightweight tenant reference used when an aggregate needs to carry
/// tenant identity + type without loading the full <see cref="Entities.Tenant"/>
/// row (e.g., when issuing a JWT whose <c>tenant_type</c> claim needs the
/// type string).
/// </summary>
public sealed class TenantReference : IEquatable<TenantReference>
{
    public Guid Id { get; }
    public TenantType Type { get; }

    public TenantReference(Guid id, TenantType type)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id cannot be empty.", nameof(id));
        }
        Id = id;
        Type = type;
    }

    public bool Equals(TenantReference? other) =>
        other is not null && Id == other.Id && Type == other.Type;

    public override bool Equals(object? obj) => obj is TenantReference other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, Type);
}
