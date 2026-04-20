using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Identity.Seed;

/// <summary>
/// T045 — Seeds the 8 system roles. Idempotent: only inserts rows that
/// don't already exist (matched by normalized name).
/// </summary>
public sealed class RoleSeeder
{
    public static readonly IReadOnlyList<RoleSeedDefinition> Definitions = new RoleSeedDefinition[]
    {
        new("super-admin", RoleScope.Platform, "Platform owner; unrestricted access."),
        new("platform-operator", RoleScope.Platform, "Operations and support team."),
        new("curriculum-admin", RoleScope.Platform, "Content ingestion pipeline manager."),
        new("subject-expert", RoleScope.Platform, "Content reviewer."),
        new("school-admin", RoleScope.School, "School customer administrator."),
        new("teacher", RoleScope.School, "School staff."),
        new("parent", RoleScope.Family, "Family account holder."),
        new("student", RoleScope.Family, "Learner."),
    };

    private readonly MuallimiDbContext _db;

    public RoleSeeder(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<int> EnsureSeededAsync(CancellationToken ct = default)
    {
        var existing = await _db.IdentityRoles
            .Select(r => r.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var inserted = 0;
        foreach (var def in Definitions)
        {
            if (existingSet.Contains(def.Name)) continue;
            _db.IdentityRoles.Add(new Role
            {
                Id = Guid.NewGuid(),
                Name = def.Name,
                Description = def.Description,
                Scope = def.Scope,
                IsSystem = true,
                CreatedAt = DateTime.UtcNow,
            });
            inserted++;
        }
        if (inserted > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return inserted;
    }
}

public sealed record RoleSeedDefinition(string Name, RoleScope Scope, string Description);
