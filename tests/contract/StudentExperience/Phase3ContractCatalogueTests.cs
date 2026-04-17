using System;
using System.IO;
using System.Linq;
using Muallimi.Api.StudentExperience.Contracts;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience;

/// <summary>
/// T133 — Phase 3 contract catalogue lock.
///
/// Three invariants keep <see cref="Phase3ContractCatalogue"/> aligned with
/// the published specs and the root <c>CLAUDE.md</c>:
///
///   1. Exactly seven contracts are catalogued (matches the spec set under
///      <c>specs/005-student-learning-experience/contracts/</c>).
///   2. Every <c>SpecFile</c> path actually resolves on disk in the
///      planning repo (planning docs live one repo up).
///   3. Every cataloged <c>ContractId</c> appears in the planning repo's
///      <c>CLAUDE.md</c> Phase 3 contract catalogue section.
///
/// A drift in any of these fails CI before the Phase 3 readiness sign-off.
/// </summary>
public class Phase3ContractCatalogueTests
{
    private static readonly string[] ExpectedContractIds = new[]
    {
        "student.experience.home",
        "student.lesson.retrieval",
        "student.tutor.chat",
        "student.quiz.mock_test",
        "student.homework_help",
        "student.whiteboard",
        "student.session_events",
    };

    [Fact]
    public void Catalogue_Lists_Exactly_Seven_Phase3_Contracts()
    {
        Assert.Equal(7, Phase3ContractCatalogue.All.Count);
        var ids = Phase3ContractCatalogue.All.Select(c => c.ContractId).OrderBy(x => x).ToArray();
        Assert.Equal(ExpectedContractIds.OrderBy(x => x).ToArray(), ids);
    }

    [Fact]
    public void Every_Owner_Repository_Is_The_Main_Backend()
    {
        // Phase 3 is a consumer phase. Every new contract is owned by
        // main-backend; ai-service and document-ingestion stay unchanged.
        Assert.All(Phase3ContractCatalogue.All,
            c => Assert.Equal("muallimi-main-backend", c.OwnerRepository));
    }

    [Fact]
    public void Every_SpecFile_Resolves_On_Disk_In_The_Planning_Repo()
    {
        var planningRepoRoot = LocatePlanningRepoRoot();
        if (planningRepoRoot is null)
        {
            // Planning repo is expected to sit as a sibling of the backend
            // repo. When it is not present locally (e.g. CI pulled only the
            // backend repo), skip instead of failing — the catalogue is
            // still structurally validated by the other two tests.
            return;
        }

        foreach (var contract in Phase3ContractCatalogue.All)
        {
            var path = Path.Combine(planningRepoRoot, contract.SpecFile);
            Assert.True(
                File.Exists(path),
                $"contract spec file missing on disk: {contract.ContractId} -> {path}");
        }
    }

    [Fact]
    public void CLAUDE_md_Lists_Every_Catalogued_ContractId()
    {
        var planningRepoRoot = LocatePlanningRepoRoot();
        if (planningRepoRoot is null) return;

        var claudeMdPath = Path.Combine(planningRepoRoot, "CLAUDE.md");
        Assert.True(File.Exists(claudeMdPath), $"CLAUDE.md not found at {claudeMdPath}");
        var claudeMd = File.ReadAllText(claudeMdPath);

        foreach (var contract in Phase3ContractCatalogue.All)
        {
            Assert.Contains(
                contract.ContractId,
                claudeMd);
        }
    }

    /// <summary>
    /// Walks up from the test assembly location looking for a sibling
    /// folder whose <c>CLAUDE.md</c> is the planning repo's (identified by
    /// the marker phrase "Muaallimi Platform Planning Docs"). Returns null
    /// when the planning repo is not present locally.
    /// </summary>
    private static string? LocatePlanningRepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        var rootGuard = Path.GetPathRoot(directory) ?? "/";
        while (directory is not null && directory != rootGuard)
        {
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent == directory) break;
            // Scan siblings of the current directory.
            try
            {
                foreach (var sibling in Directory.EnumerateDirectories(parent))
                {
                    var candidate = Path.Combine(sibling, "CLAUDE.md");
                    if (!File.Exists(candidate)) continue;
                    var head = File.ReadAllText(candidate);
                    if (head.Contains("Muaallimi Platform Planning Docs"))
                    {
                        return sibling;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip unreadable siblings and keep walking.
            }
            directory = parent;
        }
        return null;
    }
}
