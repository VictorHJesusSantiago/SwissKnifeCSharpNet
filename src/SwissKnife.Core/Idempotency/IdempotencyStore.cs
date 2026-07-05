using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Idempotency;

public sealed record IdempotentResponse(int StatusCode, string? BodyJson);

/// <summary>FND-039: deduplicação de comandos mutáveis via header Idempotency-Key.</summary>
public sealed class IdempotencyStore(SwissKnifeDbContext db)
{
    public async Task<IdempotentResponse?> TryGetAsync(string key, Guid tenantId, string endpoint, string requestHash, CancellationToken cancellationToken = default)
    {
        var entity = await db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key && x.TenantId == tenantId && x.Endpoint == endpoint, cancellationToken);
        if (entity is null) return null;
        if (entity.ExpiresAt < DateTimeOffset.UtcNow) return null;
        if (entity.RequestHash != requestHash)
            throw new InvalidOperationException("A Idempotency-Key foi reutilizada com um corpo de requisição diferente.");
        return new IdempotentResponse(entity.ResponseStatusCode, entity.ResponseBodyJson);
    }

    public async Task SaveAsync(string key, Guid tenantId, string endpoint, string requestHash, int statusCode, string? bodyJson, CancellationToken cancellationToken = default)
    {
        db.IdempotencyKeys.Add(new IdempotencyKeyEntity
        {
            Key = key,
            TenantId = tenantId,
            Endpoint = endpoint,
            RequestHash = requestHash,
            ResponseStatusCode = statusCode,
            ResponseBodyJson = bodyJson,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
