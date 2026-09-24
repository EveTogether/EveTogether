using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Runs.Repositories.Implementations;

/// <summary>
/// This character's own runs, through the same delete the runs screen uses: a run shared with the fleet has to tell
/// the server it is gone, and the activity summaries it fed have to be rebuilt. Another pilot's run in the same group
/// is their own row and is never touched.
/// </summary>
[ClientOnly]
internal sealed class RunsCharacterDataEraser(
    IDbContextFactory<ClientDbContext> contextFactory, IServiceScopeFactory scopes) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.History;

    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        List<Guid> runIds;
        await using (ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken))
            runIds = await db.Set<Run>()
                .Where(run => run.CharacterId == characterId && !run.DeletedAtUtc.HasValue)
                .Select(run => run.Id)
                .ToListAsync(cancellationToken);

        using IServiceScope scope = scopes.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        DateTime deletedAtUtc = DateTime.UtcNow;
        foreach (Guid runId in runIds)
            await dispatcher.Send(new DeleteRunCommand(runId, deletedAtUtc), cancellationToken);
    }
}
