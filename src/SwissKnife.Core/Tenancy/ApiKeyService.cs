using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Tenancy;

public sealed record ApiKeyIssued(Guid Id, string PlainTextKey, string Prefix);

/// <summary>
/// FND-031 + API-015/016: gera, resolve e revoga API keys vinculadas a tenants e escopos.
/// A chave nunca é armazenada em claro — só o hash SHA-256 é persistido.
/// </summary>
public sealed class ApiKeyService(SwissKnifeDbContext db)
{
    public async Task<ApiKeyIssued> IssueAsync(Guid tenantId, string name, string scopes, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        var plain = $"sk_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var prefix = plain[..10];
        var entity = new ApiKeyEntity
        {
            TenantId = tenantId,
            Name = name,
            KeyHash = Hash(plain),
            KeyPrefix = prefix,
            Scopes = scopes,
            ExpiresAt = expiresAt
        };
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new ApiKeyIssued(entity.Id, plain, prefix);
    }

    public async Task<bool> RevokeAsync(Guid apiKeyId, CancellationToken cancellationToken = default)
    {
        var entity = await db.ApiKeys.FindAsync([apiKeyId], cancellationToken);
        if (entity is null) return false;
        entity.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ApiKeyEntity?> ResolveAsync(string plainTextKey, CancellationToken cancellationToken = default)
    {
        var hash = Hash(plainTextKey);
        var entity = await db.ApiKeys.FirstOrDefaultAsync(x => x.KeyHash == hash, cancellationToken);
        if (entity is null || !entity.IsActive(DateTimeOffset.UtcNow)) return null;
        entity.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
