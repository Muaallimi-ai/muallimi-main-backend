using System;

namespace Muallimi.Domain.Curriculum;

public class Phase1DownstreamEvent
{
    public Guid Phase1DownstreamEventId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string DeliveryState { get; set; } = "queued";
    public int DispatchAttempts { get; set; }
}
