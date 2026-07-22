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

    /// <summary>Null quando o usuário só existe para provisionamento futuro via OIDC (API-010/011) — API-012: login local opcional.</summary>
    public string? PasswordHash { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>API-013: MFA (TOTP) para administradores locais.</summary>
    public bool MfaEnabled { get; set; }
    public string? MfaSecretProtected { get; set; }
    public string? MfaRecoveryCodesProtected { get; set; }

    /// <summary>API-018/019: papéis/escopos do usuário, mesmo formato de ApiKeyEntity.Scopes.</summary>
    public string Scopes { get; set; } = "";

    public IReadOnlyList<string> ScopeList() =>
        Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool HasScope(string scope) =>
        Scopes.Trim() == "*" || ScopeList().Any(s => s == "*" || s.Equals(scope, StringComparison.OrdinalIgnoreCase));
}

public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>API-021: elevação temporária de privilégio com justificativa, auditada e com expiração.</summary>
public sealed class TemporaryElevationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string GrantedScope { get; set; }
    public required string Justification { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>API-023: auditoria de login, acesso administrativo, exportação e mudanças sensíveis.</summary>
public sealed class AuditLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? TenantId { get; set; }
    public string? ActorName { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? DetailsJson { get; set; }
    public string? IpAddress { get; set; }
    public bool Success { get; set; } = true;
}

/// <summary>API-033: feature flags globais, por tenant e por ambiente.</summary>
public sealed class FeatureFlagEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Key { get; set; }
    public Guid? TenantId { get; set; }
    public string? Environment { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}

/// <summary>API-034: configuração dinâmica com histórico e possibilidade de rollback.</summary>
public sealed class DynamicConfigEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}

public sealed class DynamicConfigHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public int Version { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ChangedBy { get; set; }
}
