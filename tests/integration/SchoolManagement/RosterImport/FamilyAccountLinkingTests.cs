using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.RosterImport;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement.RosterImport;

/// <summary>
/// T052 (US2) — Integration test for existing family-account linking.
///
/// When a parent identity derived from (school_tenant_id, parent_email)
/// already has a <see cref="ParentProfile"/> row (simulating a parent
/// who has a pre-existing family account at the school), the roster
/// import reuses that row and adds a new <see cref="ChildLink"/> to the
/// imported student. No duplicate <see cref="ParentProfile"/> is created.
/// </summary>
public class FamilyAccountLinkingTests
{
    [Fact]
    public async Task Existing_Family_Account_Is_Linked_Not_Duplicated()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId);

        // Pre-seed a family-account parent whose identity derives from
        // the same (school_tenant, email) pair the roster row will use.
        var parentIdentityId = StudentProfileLinker.DeriveGuid($"{schoolTenantId}|parent|parent@example.test");
        var preExistingParentProfileId = Guid.NewGuid();
        db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = preExistingParentProfileId,
            TenantId = tenantId,
            IdentityId = parentIdentityId,
            PreferredLanguage = "ar",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var files = new InMemoryRosterFileStore();
        var blob = "roster.csv";
        await files.WriteAsync(blob, Encoding.UTF8.GetBytes(
            "student_name_ar,student_name_en,grade,parent_name,parent_email\n" +
            "محمد,Mohammed,7,والد محمد,parent@example.test\n"));

        var rosterImportId = Guid.NewGuid();
        db.RosterImports.Add(new Muallimi.Domain.SchoolManagement.RosterImport
        {
            RosterImportId = rosterImportId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = Guid.NewGuid(),
            SourceFileBlobKey = blob,
            OriginalFileName = "roster.csv",
            Status = "uploaded",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var worker = new RosterImportWorker(
            new RosterImportRepository(db),
            files,
            new RosterFileParser(),
            new RosterRowValidator(),
            new StudentProfileLinker(db),
            new Phase5DownstreamEventOutbox(db),
            db);

        await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blob, "corr"),
            CancellationToken.None);

        var parentCount = await db.ParentProfiles
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.IdentityId == parentIdentityId)
            .CountAsync();
        Assert.Equal(1, parentCount);

        var childLinks = await db.ChildLinks
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId && l.ParentProfileId == preExistingParentProfileId)
            .ToListAsync();
        Assert.Single(childLinks);
    }

    private static void SeedSchool(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId)
    {
        db.SchoolTenants.Add(new Muallimi.Domain.SchoolManagement.SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = "مدرسة",
            SchoolNameEn = "School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}
