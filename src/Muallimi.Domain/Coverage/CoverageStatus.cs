using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Coverage;

public class CoverageStatus
{
    public Guid LessonId { get; set; }
    public AssetType AssetType { get; set; }
    public CoverageState State { get; set; }
    public long? QueueAge { get; set; }
    public string? Owner { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}
