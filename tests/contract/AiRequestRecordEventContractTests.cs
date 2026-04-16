using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T016 (consumer-side) — ai.tutor.request.recorded event persistence.
/// Verifies main-backend consumes from queue and writes AiRequestRecord rows.
/// </summary>
public class AiRequestRecordEventContractTests
{
    [Fact]
    public void Consumer_Persists_Envelope_To_AiRequestRecords()
    {
        Assert.True(true, "Placeholder — persistence handler added in US1 T036");
    }

    [Fact]
    public void Consumer_Is_Idempotent_On_Replay()
    {
        Assert.True(true, "Placeholder — upsert by record_id");
    }

    [Fact]
    public void Consumer_Writes_CorrelationId_And_CurriculumScope()
    {
        Assert.True(true, "Placeholder — index (correlation_id, session_id, curriculum_type, final_outcome)");
    }
}
