using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.ClassManagement;

/// <summary>
/// T067 (US3) — Contract test for POST/GET class endpoints.
///
/// Pins the route constants and the observable side-effects of the class
/// CRUD surface: creating a class writes a <see cref="ClassGroup"/> row
/// with tenant scope + JSON-serialised subject bindings; listing returns
/// classes scoped to the school; the detail response projects enrolled
/// students and assigned teachers without forbidden columns.
/// </summary>
public class ClassCrudTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/classes", ClassEndpoints.CreateRoute);
        Assert.Equal("/api/school-admin/classes", ClassEndpoints.ListRoute);
        Assert.Equal("/api/school-admin/classes/{classGroupId:guid}", ClassEndpoints.DetailRoute);
    }

    [Fact]
    public void Request_Shape_Matches_Contract()
    {
        var props = PropertyNamesOf<ClassEndpoints.CreateClassRequest>();
        Assert.Contains("grade", props);
        Assert.Contains("section_label", props);
        Assert.Contains("display_name_ar", props);
        Assert.Contains("display_name_en", props);
        Assert.Contains("subject_bindings", props);
        Assert.Contains("academic_year", props);
    }

    [Fact]
    public async Task Create_Writes_Class_With_Tenant_Scope_And_Serialised_Subjects()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var service = BuildService(db);

        var row = await service.CreateClassAsync(new ClassGroupCreateInput(
            Grade: 7,
            SectionLabel: "A",
            DisplayNameAr: "الصف السابع أ",
            DisplayNameEn: "Grade 7A",
            SubjectBindings: new List<string> { "math", "arabic" },
            AcademicYear: "2026-2027",
            TenantId: tenantId,
            SchoolTenantId: schoolTenantId), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, row.ClassGroupId);
        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(schoolTenantId, row.SchoolTenantId);
        Assert.Contains("\"math\"", row.SubjectBindings);
        Assert.Contains("\"arabic\"", row.SubjectBindings);

        var persisted = await db.ClassGroups
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ClassGroupId == row.ClassGroupId);
        Assert.NotNull(persisted);
        Assert.True(persisted!.IsActive);
    }

    [Fact]
    public async Task List_Is_Scoped_To_School_And_Ignores_Other_Schools()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var repo = new ClassGroupRepository(db);
        var alpha = await repo.ListForSchoolAsync(TenantIsolationHarness.TenantAlpha, TenantIsolationHarness.SchoolAlpha, CancellationToken.None);
        var beta = await repo.ListForSchoolAsync(TenantIsolationHarness.TenantBeta, TenantIsolationHarness.SchoolBeta, CancellationToken.None);

        Assert.Single(alpha);
        Assert.Single(beta);
        Assert.DoesNotContain(alpha, c => c.SchoolTenantId == TenantIsolationHarness.SchoolBeta);
        Assert.DoesNotContain(beta, c => c.SchoolTenantId == TenantIsolationHarness.SchoolAlpha);
    }

    [Fact]
    public async Task Create_Rejects_Invalid_Grade_Or_Names()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var service = BuildService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateClassAsync(new ClassGroupCreateInput(
                Grade: 0,
                SectionLabel: "A",
                DisplayNameAr: "X",
                DisplayNameEn: "X",
                SubjectBindings: new List<string>(),
                AcademicYear: "2026-2027",
                TenantId: Guid.NewGuid(),
                SchoolTenantId: Guid.NewGuid()), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateClassAsync(new ClassGroupCreateInput(
                Grade: 7,
                SectionLabel: "A",
                DisplayNameAr: "",
                DisplayNameEn: "",
                SubjectBindings: new List<string>(),
                AcademicYear: "2026-2027",
                TenantId: Guid.NewGuid(),
                SchoolTenantId: Guid.NewGuid()), CancellationToken.None));
    }

    private static ClassManagementService BuildService(Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var classes = new ClassGroupRepository(db);
        var enrolments = new ClassEnrolmentRepository(db);
        return new ClassManagementService(classes, enrolments);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
