using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Killmails;

/// <summary>
/// What a killmail reader (kill/loss overview, kill detail, the linked-loss line) reads a character, corporation
/// or alliance id's name from. Shows the corp and alliance of the killmail's own moment, not a character's current
/// one: the ids passed in are the ones stored on the killmail itself, never looked up from a character's affiliation
/// today. Follows the <c>RunsCharacterNames</c> pattern — <see cref="HydrateAsync"/> resolves once per read what
/// is not yet known, <see cref="NameOf"/> is then a synchronous read — but backs it with the persistent
/// <see cref="KillmailEntityName"/> table instead of memory alone, so a name survives past this read.
/// </summary>
public sealed class KillmailNames(
    IReadOnlyDictionary<int, string> ownCharacterNames,
    IExternalCharacterLookup? characterLookup,
    IEsiAffiliationResolver affiliationResolver,
    ISdeAccessor sde,
    IKillmailEntityNameRepository repository,
    ISettingRepository settings,
    TimeProvider clock)
{
    /// <summary>How many days a resolved name stays fresh before a re-fetch. A setting, not a fixed value: a name
    /// changes rarely, so a generous default (30) is deliberate.</summary>
    public const string NameRefreshDaysSettingKey = "killmails.name-refresh-days";
    private const int DefaultRefreshDays = 30;

    private readonly Dictionary<long, string?> _resolved = new();

    /// <summary>The id's name: the owner's own character, an NPC corporation/faction from the SDE, a previously
    /// resolved row, or the bare id when none of those know it. Synchronous — call <see cref="HydrateAsync"/> first.</summary>
    public string NameOf(long id)
    {
        if (id <= int.MaxValue && id > 0 && ownCharacterNames.TryGetValue((int)id, out var own))
        {
            return own;
        }

        if (id <= int.MaxValue && id > 0)
        {
            var sdeName = sde.GetNpcCorporationName((int)id) ?? sde.GetFactionName((int)id);
            if (sdeName is not null)
            {
                return sdeName;
            }
        }

        return _resolved.TryGetValue(id, out var resolved) && resolved is not null ? resolved : id.ToString();
    }

    /// <summary>Resolves whatever ids are not already known: skips the owner's own characters, NPC corporations
    /// (SDE, never ESI) and any id whose row is still within the refresh interval. The rest is fetched from ESI
    /// and upserted with today's timestamp, including a miss (stored with a null name) so a 404 is not re-asked
    /// before the interval passes.</summary>
    public async Task HydrateAsync(
        IEnumerable<int> characterIds,
        IEnumerable<int> corporationIds,
        IEnumerable<int> allianceIds,
        CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - await _RefreshIntervalAsync(cancellationToken);

        int[] characters = [.. characterIds.Distinct().Where(id => id > 0 && !ownCharacterNames.ContainsKey(id))];
        int[] corporations = [.. corporationIds.Distinct().Where(id => id > 0 && sde.GetNpcCorporationName(id) is null)];
        int[] alliances = [.. allianceIds.Distinct().Where(id => id > 0)];

        long[] candidates = [.. characters.Select(id => (long)id), .. corporations.Select(id => (long)id), .. alliances.Select(id => (long)id)];
        var known = candidates.Length > 0
            ? await repository.GetManyAsync(candidates, cancellationToken)
            : new Dictionary<long, KillmailEntityName>();

        foreach (var id in characters)
        {
            await _HydrateOneAsync(id, KillmailEntityKind.Character, known, cutoff, () => _ResolveCharacterAsync(id, cancellationToken), cancellationToken);
        }
        foreach (var id in corporations)
        {
            await _HydrateOneAsync(id, KillmailEntityKind.Corporation, known, cutoff, () => affiliationResolver.ResolveCorporationNameAsync(id, cancellationToken), cancellationToken);
        }
        foreach (var id in alliances)
        {
            await _HydrateOneAsync(id, KillmailEntityKind.Alliance, known, cutoff, () => affiliationResolver.ResolveAllianceNameAsync(id, cancellationToken), cancellationToken);
        }
    }

    private async Task _HydrateOneAsync(
        int id,
        KillmailEntityKind kind,
        IReadOnlyDictionary<long, KillmailEntityName> known,
        DateTime cutoff,
        Func<Task<string?>> resolve,
        CancellationToken cancellationToken)
    {
        if (known.TryGetValue(id, out var row) && row.RefreshedAtUtc > cutoff)
        {
            _resolved[id] = row.Name;
            return; // fresh enough → no ESI call
        }

        cancellationToken.ThrowIfCancellationRequested();
        var name = await resolve();
        var refreshedAtUtc = clock.GetUtcNow().UtcDateTime;
        await repository.UpsertAsync(new KillmailEntityName { Id = id, Kind = kind, Name = name, RefreshedAtUtc = refreshedAtUtc }, cancellationToken);
        _resolved[id] = name;
    }

    private async Task<string?> _ResolveCharacterAsync(int id, CancellationToken cancellationToken)
    {
        if (characterLookup is null)
        {
            return null;
        }

        var info = await characterLookup.LookupAsync(id, cancellationToken);
        return info.Exists ? info.Name : null;
    }

    private async Task<TimeSpan> _RefreshIntervalAsync(CancellationToken cancellationToken)
    {
        var all = await settings.ListAsync(cancellationToken);
        var raw = all.FirstOrDefault(setting => setting.Key == NameRefreshDaysSettingKey)?.Value;
        var days = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultRefreshDays;
        return TimeSpan.FromDays(days);
    }
}
