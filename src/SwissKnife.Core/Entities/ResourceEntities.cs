namespace SwissKnife.Core.Entities;

public sealed class Resource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Module { get; set; }
    public required string Name { get; set; }
    public string Status { get; set; } = "active";
    public Guid? OwnerUserId { get; set; }
    public Guid? TeamOrgUnitId { get; set; }
    public string? CostCenter { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public int SchemaVersion { get; set; } = 1;
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    public List<ResourceTag> Tags { get; set; } = [];
}

public sealed class ResourceTag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public required string Tag { get; set; }
}

public sealed class ResourceComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public Guid? AuthorUserId { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EditedAt { get; set; }
}

public enum AttachmentScanStatus { Pending, Clean, Infected, Skipped }

public sealed class ResourceAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256Hash { get; set; }
    public required string StoragePath { get; set; }
    public AttachmentScanStatus ScanStatus { get; set; } = AttachmentScanStatus.Pending;
    public string? UploadedBy { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ResourceRelationType { DependsOn, PartOf, RelatesTo, Blocks }

public sealed class ResourceRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceResourceId { get; set; }
    public Guid TargetResourceId { get; set; }
    public ResourceRelationType RelationType { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ResourceChangeKind { Create, Update, Delete, Restore }

public sealed class ResourceHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public int Version { get; set; }
    public required string PayloadJsonSnapshot { get; set; }
    public required string Status { get; set; }
    public string? ChangedBy { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public ResourceChangeKind ChangeKind { get; set; }
}

public sealed class ResourceStateTransition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Module { get; set; }
    public required string FromState { get; set; }
    public required string ToState { get; set; }
    public string? AllowedRoles { get; set; }
}

public sealed class CustomFieldDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Module { get; set; }
    public required string FieldName { get; set; }
    public string FieldType { get; set; } = "string";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
}

public sealed class CustomFieldValue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public Guid FieldDefinitionId { get; set; }
    public string? Value { get; set; }
}

public sealed class ResourceTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public required string Module { get; set; }
    public required string Name { get; set; }
    public required string PayloadJsonTemplate { get; set; }
}

public sealed class RetentionPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public required string Module { get; set; }
    public int RetainDeletedDays { get; set; } = 30;
}

public sealed class SavedSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Name { get; set; }
    public required string FilterJson { get; set; }
    public bool IsFavorite { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ResourceExternalKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public required string ExternalKey { get; set; }
    public required string Source { get; set; }
}

public enum ImportFormat { Csv, Json, Yaml }
public enum ImportJobStatus { Running, Completed, Failed }

public sealed class ImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Module { get; set; }
    public ImportFormat Format { get; set; }
    public ImportJobStatus Status { get; set; } = ImportJobStatus.Running;
    public string? SourceFileHash { get; set; }
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int ConflictCount { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class ImportConflict
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ImportJobId { get; set; }
    public int RowNumber { get; set; }
    public required string Reason { get; set; }
    public string? RawData { get; set; }
    public Guid? ExistingResourceId { get; set; }
}
