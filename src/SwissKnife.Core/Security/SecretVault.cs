using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Security;

/// <summary>
/// FND-037: cofre de segredos por referência. O valor bruto nunca é devolvido pela leitura
/// normal (Get/List) — só o endpoint explícito de "reveal", com escopo elevado e auditoria,
/// decripta o valor. Criptografia via Microsoft.AspNetCore.DataProtection (sem KMS externo,
/// decisão documentada no plano da fundação).
/// </summary>
public interface ISecretVault
{
    Task<Guid> StoreAsync(Guid tenantId, string name, string plainTextValue, CancellationToken cancellationToken = default);
    Task<string?> RevealAsync(Guid tenantId, Guid secretId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecretReferenceEntity>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task RotateAsync(Guid tenantId, Guid secretId, string newPlainTextValue, CancellationToken cancellationToken = default);
}

public sealed class DataProtectionSecretVault(SwissKnifeDbContext db, IDataProtectionProvider provider) : ISecretVault
{
    private readonly IDataProtector _protector = provider.CreateProtector("SwissKnife.SecretVault.v1");

    public async Task<Guid> StoreAsync(Guid tenantId, string name, string plainTextValue, CancellationToken cancellationToken = default)
    {
        var entity = new SecretReferenceEntity
        {
            TenantId = tenantId,
            Name = name,
            ProtectedValue = _protector.Protect(plainTextValue)
        };
        db.SecretReferences.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<string?> RevealAsync(Guid tenantId, Guid secretId, CancellationToken cancellationToken = default)
    {
        var entity = await db.SecretReferences.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == secretId && x.TenantId == tenantId, cancellationToken);
        return entity is null ? null : _protector.Unprotect(entity.ProtectedValue);
    }

    public async Task<IReadOnlyList<SecretReferenceEntity>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await db.SecretReferences.AsNoTracking().Where(x => x.TenantId == tenantId).ToListAsync(cancellationToken);

    public async Task RotateAsync(Guid tenantId, Guid secretId, string newPlainTextValue, CancellationToken cancellationToken = default)
    {
        var entity = await db.SecretReferences.FirstOrDefaultAsync(x => x.Id == secretId && x.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Segredo não encontrado.");
        entity.ProtectedValue = _protector.Protect(newPlainTextValue);
        entity.RotatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
