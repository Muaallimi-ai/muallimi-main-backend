using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.RosterImport;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement.RosterImport;

/// <summary>
/// T050 (US2) — Integration test for duplicate detection and Arabic
/// name normalisation.
///
/// Two rows differing only in Arabic orthography variants (alif hamza
/// variants, ya vs alif maqsura, diacritics) collapse to the same
/// deduplication key and therefore only one student is created; the
/// second row is reported as a skip, not an error. The stored
/// <c>DisplayName</c> still reflects the ORIGINAL glyph sequence of the
/// first row — normalisation is only used to compute the dedup key.
/// </summary>
public class DuplicateDetectionTests
{
    [Fact]
    public async Task Arabic_Name_Variants_Collapse_To_A_Single_Student()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId);

        var blobKey = "dup.csv";
        var files = new InMemoryRosterFileStore();
        await files.WriteAsync(blobKey, Encoding.UTF8.GetBytes(
            "student_name_ar,student_name_en,grade,parent_name,parent_email\n" +
            "أحمد الشامي,Ahmed,7,أب,p@example.test\n" +      // alif hamza
            "احمدُ الشَامِى,Ahmed,7,أب,p@example.test\n" +   // bare alif + ya maqsura + diacritics
            "فاطمة,Fatima,7,أب,f@example.test\n"));

        var rosterImportId = await SeedRosterImportRow(db, tenantId, schoolTenantId, blobKey);
        var worker = BuildWorker(db, files);
        var outcome = await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, "corr-dup"),
            CancellationToken.None);

        Assert.Equal(3, outcome.TotalRowCount);
        Assert.Equal(2, outcome.SuccessCount);
        Assert.Equal(1, outcome.SkipCount);
        Assert.Equal(0, outcome.ErrorCount);

        var students = await db.StudentProfiles
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync();
        Assert.Equal(2, students.Count);

        // The first row's Arabic glyphs are preserved verbatim (diacritics,
        // hamza form intact) — normalisation is for the dedup key only.
        Assert.Contains(students, s => s.DisplayName == "أحمد الشامي");
    }

    private static RosterImportWorker BuildWorker(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        InMemoryRosterFileStore files)
    {
        return new RosterImportWorker(
            new RosterImportRepository(db),
            files,
            new RosterFileParser(),
            new RosterRowValidator(),
            new StudentProfileLinker(db),
            new Phase5DownstreamEventOutbox(db),
            db);
    }

    private static async Task<Guid> SeedRosterImportRow(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId, string blobKey)
    {
        var id = Guid.NewGuid();
        db.RosterImports.Add(new Muallimi.Domain.SchoolManagement.RosterImport
        {
            RosterImportId = id,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = Guid.NewGuid(),
            SourceFileBlobKey = blobKey,
            OriginalFileName = "dup.csv",
            Status = "uploaded",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
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
