using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement;

/// <summary>
/// T018 — <c>SchoolRoleClaimMiddleware</c>.
///
/// Extends Phase 0 identity claims with scoped Phase 5 roles:
///   - <c>school_admin:{school_tenant_id}</c> — granted to
///     <see cref="SchoolAdministrator"/> rows whose onboarding_status is
///     <c>onboarded</c>.
///   - <c>teacher:{school_tenant_id}:{class_id}:{subject_id}</c> — granted
///     per active <see cref="TeacherAssignment"/> row (one claim per class+
///     subject pair).
///
/// Runs after authentication but before authorization so role-based
/// endpoints can rely on the claim being populated on the same request.
/// </summary>
public sealed class SchoolRoleClaimMiddleware
{
    private readonly RequestDelegate _next;

    public SchoolRoleClaimMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, MuallimiDbContext db)
    {
        var userIdentityClaim = context.User?.Claims.FirstOrDefault(c =>
            string.Equals(c.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal));
        if (userIdentityClaim is not null
            && Guid.TryParse(userIdentityClaim.Value, out var userIdentityId)
            && context.User?.Identity is ClaimsIdentity identity)
        {
            var adminBindings = await db.SchoolAdministrators
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(a => a.UserIdentityId == userIdentityId
                            && a.OnboardingStatus == "onboarded"
                            && a.DeactivatedAt == null)
                .Select(a => a.SchoolTenantId)
                .ToListAsync(context.RequestAborted);
            foreach (var schoolId in adminBindings)
            {
                identity.AddClaim(new Claim($"school_admin:{schoolId}", "true"));
            }

            var teacherAssignments = await (
                from t in db.Teachers.AsNoTracking().IgnoreQueryFilters()
                join ta in db.TeacherAssignments.AsNoTracking().IgnoreQueryFilters()
                    on t.TeacherId equals ta.TeacherId
                where t.UserIdentityId == userIdentityId
                      && t.DeactivatedAt == null
                      && ta.UnassignedAt == null
                select new { t.SchoolTenantId, ta.ClassGroupId, ta.SubjectId }
            ).ToListAsync(context.RequestAborted);

            foreach (var a in teacherAssignments)
            {
                identity.AddClaim(new Claim($"teacher:{a.SchoolTenantId}:{a.ClassGroupId}:{a.SubjectId}", "true"));
            }
        }

        await _next(context);
    }
}

public static class SchoolRoleClaimMiddlewareApplicationBuilderExtensions
{
    public static IApplicationBuilder UseSchoolRoleClaims(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SchoolRoleClaimMiddleware>();
    }
}
