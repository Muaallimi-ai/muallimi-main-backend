using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolTenant;

/// <summary>
/// T029 (US1) — Contract test for POST <c>/operator/schools</c>.
///
/// Pins:
///   • the endpoint route constant so the frontend operator surface and
///     the Phase 6 operator tooling can bind with confidence;
///   • the request payload shape from the
///     <c>specs/007-school-management-b2b/contracts/school-tenant-contract.md</c>
///     file;
///   • the response payload shape;
///   • the observable side-effect that a new school tenant row AND a
///     <c>school_created</c> downstream event both land within the same
///     unit of work (T041).
/// </summary>
public class CreateSchoolTenantTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/operator/schools", SchoolTenantEndpoints.Route);
    }

    [Fact]
    public void Request_Shape_Matches_Contract()
    {
        var props = PropertyNamesOf<SchoolTenantEndpoints.CreateSchoolRequest>();
        Assert.Contains("school_name_ar", props);
        Assert.Contains("school_name_en", props);
        Assert.Contains("curriculum_type", props);
        Assert.Contains("grade_range_start", props);
        Assert.Contains("grade_range_end", props);
        Assert.Contains("subject_bindings", props);
        Assert.Contains("academic_calendar", props);
        Assert.Contains("preferred_language", props);
    }

    [Fact]
    public async Task Create_Writes_Tenant_And_Emits_School_Created_Event()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolTenantRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var service = new SchoolTenantProvisioningService(repo, outbox);

        var operatorId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var tenant = await service.CreateAsync(
            new SchoolTenantCreateInput(
                SchoolNameAr: "مدرسة المثال",
                SchoolNameEn: "Example School",
                CurriculumType: "moe",
                GradeRangeStart: 1,
                GradeRangeEnd: 12,
                SubjectBindings: new List<string> { "math", "arabic" },
                AcademicCalendar: new { terms = Array.Empty<object>() },
                PreferredLanguage: "ar",
                CreatedByOperatorId: operatorId,
                TenantId: tenantId),
            correlationId: "corr-001",
            ct: CancellationToken.None);

        Assert.NotEqual(Guid.Empty, tenant.SchoolTenantId);
        Assert.Equal("trial", tenant.SubscriptionStatus);
        Assert.Equal(operatorId, tenant.CreatedByOperatorId);

        var persisted = await db.SchoolTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SchoolTenantId == tenant.SchoolTenantId);
        Assert.NotNull(persisted);

        var evt = await db.Phase5DownstreamEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.SchoolTenantId == tenant.SchoolTenantId);
        Assert.NotNull(evt);
        Assert.Equal(Phase5DownstreamEventKind.school_created.ToString(), evt!.EventKind);
        Assert.Equal("corr-001", evt.CorrelationId);
        Assert.Equal("queued", evt.DeliveryState);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
