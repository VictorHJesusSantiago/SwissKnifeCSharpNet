using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Auditing;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Auth;

/// <summary>API-021: elevação temporária de privilégio com justificativa obrigatória e expiração automática.</summary>
public sealed class ElevationService(SwissKnifeDbContext db, AuditLogger audit)
{
    public async Task<TemporaryElevationEntity> GrantAsync(Guid userId, string scope, string justification, TimeSpan duration, string? approvedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("Justificativa é obrigatória para elevação temporária de privilégio.");

        var elevation = new TemporaryElevationEntity
        {
            UserId = userId,
            GrantedScope = scope,
            Justification = justification,
            ApprovedBy = approvedBy,
            ExpiresAt = DateTimeOffset.UtcNow.Add(duration)
        };
        db.Set<TemporaryElevationEntity>().Add(elevation);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync(null, approvedBy, "elevation.granted", "User", userId.ToString(),
            new { scope, justification, expiresAt = elevation.ExpiresAt }, cancellationToken: cancellationToken);
        return elevation;
    }

    public async Task<IReadOnlyList<string>> GetActiveScopesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.Set<TemporaryElevationEntity>().AsNoTracking()
            .Where(x => x.UserId == userId && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(x => x.GrantedScope)
            .ToListAsync(cancellationToken);

    public async Task<bool> RevokeAsync(Guid elevationId, CancellationToken cancellationToken = default)
    {
        var elevation = await db.Set<TemporaryElevationEntity>().FirstOrDefaultAsync(x => x.Id == elevationId, cancellationToken);
        if (elevation is null) return false;
        elevation.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
