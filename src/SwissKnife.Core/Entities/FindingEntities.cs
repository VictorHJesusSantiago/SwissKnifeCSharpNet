namespace SwissKnife.Core.Entities;

public enum FindingStatus { Open, Acknowledged, RiskAccepted, FalsePositive, Resolved }

public sealed class FindingEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Module { get; set; }
    public required string Code { get; set; }
    public required string Fingerprint { get; set; }
    public required string Severity { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public Guid? ResourceId { get; set; }
    public FindingStatus Status { get; set; } = FindingStatus.Open;
    public string? EvidenceJson { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Owner { get; set; }
    public string? DecisionReason { get; set; }
    public DateTimeOffset? DecisionExpiresAt { get; set; }
    public Guid? LinkedTicketId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
