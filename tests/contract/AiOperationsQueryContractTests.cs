using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T021 — AI operations query contract placeholder. The concrete assertions
/// live in T096 (<see cref="AiOperationsQueryEndpointsTests"/>) and T097
/// (<see cref="QuestionTextRedactionTests"/>). This class stays as a seam
/// that names the foundational contract id so future structural changes
/// can be found via a single reference.
/// </summary>
public class AiOperationsQueryContractTests
{
    [Fact]
    public void Phase2_Foundational_Contract_Seam_Exists()
    {
        // The aggregation shape and role redaction are covered by:
        //  - AiOperationsQueryEndpointsTests (T096)
        //  - QuestionTextRedactionTests (T097)
        // Keeping this seam lets dependents lock on the T021 contract name.
        Assert.True(true);
    }
}
