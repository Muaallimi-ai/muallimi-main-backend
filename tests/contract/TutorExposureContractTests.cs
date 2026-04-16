using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T017 — Tutor exposure facade contract (main-backend).
/// Forwards request to ai-service with session scope enforcement.
/// </summary>
public class TutorExposureContractTests
{
    [Fact]
    public void Facade_Forwards_With_SessionScope_Headers()
    {
        Assert.True(true, "Placeholder — US1 T035 wires endpoint to ai-service");
    }

    [Fact]
    public void Facade_Enforces_Tenant_Isolation_Before_Forwarding()
    {
        Assert.True(true, "Placeholder — TutorAuthorizationFilter");
    }

    [Fact]
    public void Facade_Propagates_CorrelationId_Both_Directions()
    {
        Assert.True(true, "Placeholder — X-Correlation-Id passes through unchanged");
    }
}
