namespace SwissKnife.Core.Entities;

public enum TenantStatus { Active, Suspended, Deleted }

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Slug { get; set; }
    public required string DisplayName { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string PlanTier { get; set; } = "standard";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SuspendedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public List<TenantSetting> Settings { get; set; } = [];
    public List<TenantLimit> Limits { get; set; } = [];
}

public sealed class TenantSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Key { get; set; }
    public string? Value { get; set; }
    public string ValueType { get; set; } = "string";
}

public sealed class TenantLimit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string ResourceType { get; set; }
    public int? MaxCount { get; set; }
    public long? MaxStorageBytes { get; set; }
    public int? MaxJobsConcurrent { get; set; }
}

public sealed class OrgUnit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public OrgUnitKind Kind { get; set; }
    public Guid? ParentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum OrgUnitKind { Organization, Team, Project, Environment }

public enum ApiKeyStatus { Active, Revoked, Expired }

public sealed class ApiKeyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string KeyHash { get; set; }
    public required string KeyPrefix { get; set; }
    public string Scopes { get; set; } = "*";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public IReadOnlyList<string> ScopeList() =>
        Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool HasScope(string scope) =>
        Scopes.Trim() == "*" || ScopeList().Any(s => s == "*" || s.Equals(scope, StringComparison.OrdinalIgnoreCase));
}

public sealed class UserEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
