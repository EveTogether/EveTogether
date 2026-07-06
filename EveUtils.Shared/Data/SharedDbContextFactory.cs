using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Data;

/// <summary>
/// Provides an <see cref="IDbContextFactory{TContext}"/> for the abstract <see cref="SharedDbContext"/>
/// on top of a concrete factory (Client/Server), so module repositories work against
/// <see cref="SharedDbContext"/> while each host supplies its own concrete context and provider.
/// </summary>
public sealed class SharedDbContextFactory(
    Func<SharedDbContext> create,
    Func<CancellationToken, Task<SharedDbContext>> createAsync) : IDbContextFactory<SharedDbContext>
{
    public SharedDbContext CreateDbContext() => create();

    public async Task<SharedDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        await createAsync(cancellationToken);
}
