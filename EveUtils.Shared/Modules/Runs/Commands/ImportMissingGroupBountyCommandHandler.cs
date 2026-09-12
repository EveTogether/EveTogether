using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class ImportMissingGroupBountyCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher, ICharacterRegistry characters)
    : ICommandHandler<ImportMissingGroupBountyCommand, Result<int>>
{
    /// <summary>The runs already read against their gamelog without finding anything, so a later start skips them.</summary>
    public const string CheckedSettingKey = "runs.bounty-import.checked";

    public async Task<Result<int>> Handle(ImportMissingGroupBountyCommand command, CancellationToken cancellationToken = default)
    {
        long[] own = [.. (await characters.GetAllAsync(cancellationToken))
            .Select(character => character.EsiCharacterId)
            .OfType<int>()
            .Select(characterId => (long)characterId)];
        if (own.Length == 0)
            return Result<int>.Success(0);

        List<Guid> candidates = await _CandidatesAsync(own, cancellationToken);
        string? checkedValue = (await dispatcher.Query(new GetSettingsQuery(), cancellationToken))
            .FirstOrDefault(setting => setting.Key == CheckedSettingKey)?.Value;
        HashSet<Guid> alreadyChecked = [.. (checkedValue ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => Guid.TryParse(text, out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)];
        List<Guid> fresh = [.. candidates.Where(id => !alreadyChecked.Contains(id))];
        if (fresh.Count == 0)
            return Result<int>.Success(0);

        Result<int> imported = await dispatcher.Send(new ImportRunBountyCommand(fresh), cancellationToken);
        if (!imported.IsSuccess)
            return imported;

        // Whatever still has no bounty after its gamelog was read has none there to find.
        List<Guid> stillEmpty = await _CandidatesAsync(own, cancellationToken);
        await dispatcher.Send(new SetSettingCommand(CheckedSettingKey, string.Join(",", stillEmpty)), cancellationToken);
        return imported;
    }

    private async Task<List<Guid>> _CandidatesAsync(long[] own, CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue && run.GroupCode != null
                          && own.Contains(run.CharacterId) && !run.BountyEntries.Any()
                          && db.Set<Run>().Any(sibling => sibling.GroupCode == run.GroupCode && sibling.Id != run.Id
                                                          && !sibling.DeletedAtUtc.HasValue && sibling.BountyEntries.Any()))
            .Select(run => run.Id)
            .ToListAsync(cancellationToken);
    }
}
