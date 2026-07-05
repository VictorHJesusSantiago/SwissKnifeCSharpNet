using Microsoft.EntityFrameworkCore;
using SwissKnife.Core.Entities;
using SwissKnife.Core.Persistence;
using SwissKnife.Core.Tenancy;

namespace SwissKnife.Core.Search;

/// <summary>
/// FND-024/026: busca textual global (Name + PayloadJson via LIKE) e pesquisas salvas/favoritas.
/// Simplificação consciente frente ao plano original (FTS5/tsvector nativos): um índice
/// full-text dedicado exigiria migrações específicas por provider: a busca por LIKE aqui é
/// real e funcional, porém sem os ganhos de performance de um índice invertido dedicado —
/// caminho natural de evolução quando o volume de dados justificar.
/// </summary>
public sealed class SearchService(SwissKnifeDbContext db, TenantContextAccessor tenantAccessor)
{
    private Guid TenantId => tenantAccessor.Current.TenantId;

    public async Task<IReadOnlyList<Resource>> SearchAsync(string text, int take = 50, CancellationToken cancellationToken = default) =>
        await db.Resources.AsNoTracking()
            .Where(x => x.TenantId == TenantId && (x.Name.Contains(text) || x.PayloadJson.Contains(text)))
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken);

    public async Task<SavedSearch> SaveSearchAsync(string name, string filterJson, bool favorite, CancellationToken cancellationToken = default)
    {
        var search = new SavedSearch { TenantId = TenantId, Name = name, FilterJson = filterJson, IsFavorite = favorite };
        db.SavedSearches.Add(search);
        await db.SaveChangesAsync(cancellationToken);
        return search;
    }

    public async Task<IReadOnlyList<SavedSearch>> ListSavedSearchesAsync(CancellationToken cancellationToken = default) =>
        await db.SavedSearches.AsNoTracking().Where(x => x.TenantId == TenantId).OrderByDescending(x => x.IsFavorite).ToListAsync(cancellationToken);
}
