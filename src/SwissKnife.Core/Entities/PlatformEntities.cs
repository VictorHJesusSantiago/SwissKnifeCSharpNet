namespace SwissKnife.Core.Entities;

public enum JobStatus { Queued, Running, Succeeded, Failed, Cancelled }

public sealed class JobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int ProgressPercent { get; set; }
    public string? PayloadJson { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public bool CancelRequested { get; set; }
}

public sealed class ScheduledJobEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public required string Kind { get; set; }
    public required string CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class IdempotencyKeyEntity
{
    public required string Key { get; set; }
    public Guid TenantId { get; set; }
    public required string Endpoint { get; set; }
    public required string RequestHash { get; set; }
    public int ResponseStatusCode { get; set; }
    public string? ResponseBodyJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public required string EventType { get; set; }
    public Guid TenantId { get; set; }
    public Guid? ResourceId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class SecretReferenceEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string ProtectedValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RotatedAt { get; set; }
}

public enum BackupKind { Full }

public sealed class BackupRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FilePath { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
