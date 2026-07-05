using SwissKnife.Core.Persistence;

namespace SwissKnife.Core.Repositories;

public sealed class UnitOfWork(SwissKnifeDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => db.SaveChangesAsync(cancellationToken);
}
