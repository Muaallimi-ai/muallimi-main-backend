using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.AdminOnboarding;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolTenant;

/// <summary>
/// T032 (US1) — Contract test for GET / PUT <c>/school-admin/school</c>.
///
/// Pins the endpoint route and asserts that the service's
/// <c>GetConfigurationAsync</c> + <c>UpdateConfigurationAsync</c> round-trip
/// surfaces the full tenant configuration payload (curriculum type, grade
/// range, bilingual names, subject bindings, academic calendar, preferred
/// language, subscription status).
/// </summary>
public class GetSchoolConfigTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/school", SchoolConfigEndpoints.Route);
    }

    [Fact]
    public void Update_Request_Shape_Matches_Contract()
    {
        var props = PropertyNamesOf<SchoolConfigEndpoints.UpdateSchoolConfigRequest>();
        Assert.Contains("subject_bindings", props);
        Assert.Contains("academic_calendar", props);
        Assert.Contains("preferred_language", props);
    }

    [Fact]
    public async Task GetConfiguration_Returns_Full_Tenant_Record()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolTenantRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var service = new SchoolTenantProvisioningService(repo, outbox);

        var tenantId = Guid.NewGuid();
        var created = await service.CreateAsync(
            new SchoolTenantCreateInput(
                SchoolNameAr: "مدرسة المثال",
                SchoolNameEn: "Example School",
                CurriculumType: "moe",
                GradeRangeStart: 1,
                GradeRangeEnd: 12,
                SubjectBindings: new List<string> { "math" },
                AcademicCalendar: new { },
                PreferredLanguage: "ar",
                CreatedByOperatorId: Guid.NewGuid(),
                TenantId: tenantId),
            correlationId: "corr-get",
            ct: CancellationToken.None);

        var fetched = await service.GetConfigurationAsync(tenantId, created.SchoolTenantId, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal("Example School", fetched!.SchoolNameEn);
        Assert.Equal("مدرسة المثال", fetched.SchoolNameAr);
        Assert.Equal("moe", fetched.CurriculumType);
        Assert.Equal(1, fetched.GradeRangeStart);
        Assert.Equal(12, fetched.GradeRangeEnd);
        Assert.Equal("trial", fetched.SubscriptionStatus);
        Assert.Equal("ar", fetched.PreferredLanguage);
    }

    [Fact]
    public async Task UpdateConfiguration_Persists_Bindings_And_Language()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolTenantRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var service = new SchoolTenantProvisioningService(repo, outbox);

        var tenantId = Guid.NewGuid();
        var created = await service.CreateAsync(
            new SchoolTenantCreateInput(
                SchoolNameAr: "مدرسة المثال",
                SchoolNameEn: "Example School",
                CurriculumType: "moe",
                GradeRangeStart: 1,
                GradeRangeEnd: 9,
                SubjectBindings: new List<string> { "math" },
                AcademicCalendar: new { },
                PreferredLanguage: "ar",
                CreatedByOperatorId: Guid.NewGuid(),
                TenantId: tenantId),
            correlationId: "corr-upd",
            ct: CancellationToken.None);

        var updated = await service.UpdateConfigurationAsync(
            tenantId,
            created.SchoolTenantId,
            new SchoolTenantUpdateInput(
                SubjectBindings: new List<string> { "math", "arabic", "science" },
                AcademicCalendar: new { terms = new[] { new { term_name = "T1" } } },
                PreferredLanguage: "en"),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("en", updated!.PreferredLanguage);
        Assert.Contains("science", updated.SubjectBindings);
        Assert.Contains("T1", updated.AcademicCalendar);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
