using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Killmails;

/// <summary>
/// Imports a character's kills and losses: walks <c>/characters/{id}/killmails/recent/</c> until a page holds only
/// known ids, then adds each new mail from <c>/killmails/{id}/{hash}/</c> with its items and attackers, ids only.
/// Every successful import then links the character's unlinked losses to their runs (ET-331).
/// </summary>
public sealed class EsiKillmailImporter(IEsiClient esi, ILocalKillmailReader killmails, IServiceScopeFactory scopes)
{
    internal const int SecurityStatusBatchSize = 50;

    // One import per character at a time, shared across instances, so two callers never add the same mail twice.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> _importGates = new();

    public async Task<KillmailImportResult> ImportAsync(int characterId, CancellationToken cancellationToken = default)
    {
        var gate = _importGates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var newRefs = new List<EsiKillmailRef>();
            var pages = 1;
            for (var page = 1; page <= pages; page++)
            {
                var recent = await esi.GetAsync<EsiKillmailRef[]>($"/characters/{characterId}/killmails/recent/?page={page}",
                    characterId, [KillmailsScopeCatalog.ReadKillmails], cancellationToken);
                if (!recent.IsSuccess || recent.Value is null)
                {
                    return _Failure(recent.Error);
                }

                var known = await killmails.GetKnownIdsAsync(characterId,
                    recent.Value.Select(entry => entry.KillmailId).ToList(), cancellationToken);
                var unknown = recent.Value.Where(entry => !known.Contains(entry.KillmailId)).ToList();
                if (unknown.Count == 0)
                {
                    break;
                }

                newRefs.AddRange(unknown);
                pages = recent.Pages;
            }

            // ponytail: all or nothing, so a failed page or mail is retried next time instead of hiding behind a known
            // page 1; a mail that fails for good blocks the import, skip-and-log it if that ever happens.
            var fetched = new List<LocalKillmail>();
            foreach (var entry in newRefs.DistinctBy(entry => entry.KillmailId))
            {
                var detail = await esi.GetAsync<EsiKillmail>($"/killmails/{entry.KillmailId}/{entry.KillmailHash}/",
                    cancellationToken: cancellationToken);
                if (!detail.IsSuccess || detail.Value is null)
                {
                    return _Failure(detail.Error);
                }

                fetched.Add(_ToEntity(characterId, entry.KillmailHash, detail.Value));
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            return await _StoreAndLinkAsync(dispatcher, characterId, fetched, 0, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Imports one killmail by id + hash — a pasted ESI link or in-game <c>killReport:</c> link (ET-338), bypassing
    /// the 5-minute cache on <c>/characters/{id}/killmails/recent/</c>. Stored for every own character on the mail,
    /// victim or attacker; refused with nothing stored when none of them are. <see cref="StoreKillmailsCommand"/>
    /// already skips a mail already known for that character, so a mail the feed later re-discovers (or already found
    /// first) never duplicates. Links to runs the same as <see cref="ImportAsync"/> (ET-331, ET-374).
    /// </summary>
    public async Task<KillmailImportResult> ImportOneAsync(int killmailId, string hash, CancellationToken cancellationToken = default)
    {
        var detail = await esi.GetAsync<EsiKillmail>($"/killmails/{killmailId}/{hash}/", cancellationToken: cancellationToken);
        if (!detail.IsSuccess || detail.Value is null)
        {
            return _Failure(detail.Error);
        }

        EsiKillmail killmail = detail.Value;
        var mailCharacterIds = new HashSet<int>(killmail.Attackers.Select(attacker => attacker.CharacterId).OfType<int>());
        if (killmail.Victim.CharacterId is { } victimId)
        {
            mailCharacterIds.Add(victimId);
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IReadOnlyList<Character> characters =
            await scope.ServiceProvider.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken);

        List<int> ownCharacterIds = [.. characters
            .Select(character => character.EsiCharacterId)
            .OfType<int>()
            .Where(mailCharacterIds.Contains)];
        if (ownCharacterIds.Count == 0)
        {
            return new KillmailImportResult(KillmailImportStatus.NoOwnCharacter, 0, "None of your characters is on this killmail.");
        }

        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var stored = 0;
        foreach (int characterId in ownCharacterIds)
        {
            SemaphoreSlim gate = _importGates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            KillmailImportResult outcome;
            try
            {
                outcome = await _StoreAndLinkAsync(dispatcher, characterId, [_ToEntity(characterId, hash, killmail)], stored, cancellationToken);
            }
            finally
            {
                gate.Release();
            }

            if (!outcome.IsSuccess)
            {
                return outcome;
            }

            stored = outcome.ImportedCount;
        }

        return KillmailImportResult.Ok(stored);
    }

    /// <summary>
    /// Stores a fleet mate's latest full killmail share for one fleet under their character (ET-371). Every mail not yet
    /// stored for them is fetched from the public <c>/killmails/{id}/{hash}/</c> (immutable and cached, so a kill several
    /// mates share costs one request) — never their <c>/recent</c> feed. All or nothing: one failed fetch stores nothing
    /// and keeps the previous state, so the next share retries. A mail the mate is not on is left out rather than stored
    /// as theirs; that check runs when the mail is first fetched for them, so a mail they already hold is not fetched again. Not <see cref="ImportOneAsync"/>, whose refusal of a mail without an own character stays as it is.
    /// </summary>
    public async Task<KillmailImportResult> ImportFleetShareAsync(int characterId, string serverIdentity, long fleetId,
        IReadOnlyList<(int KillmailId, string Hash)> shared, CancellationToken cancellationToken = default)
    {
        var gate = _importGates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var known = await killmails.GetKnownIdsAsync(characterId, [.. shared.Select(entry => entry.KillmailId)], cancellationToken);
            var fetched = new List<LocalKillmail>();
            var notOnMail = new HashSet<int>();
            foreach (var (killmailId, hash) in shared.Where(entry => !known.Contains(entry.KillmailId)).DistinctBy(entry => entry.KillmailId))
            {
                // A mate's input goes into the ESI path: only a real killmail hash (40 hex characters) is asked for.
                if (hash.Length != 40 || !hash.All(char.IsAsciiHexDigit))
                {
                    notOnMail.Add(killmailId);
                    continue;
                }

                var detail = await esi.GetAsync<EsiKillmail>($"/killmails/{killmailId}/{hash}/", cancellationToken: cancellationToken);
                if (!detail.IsSuccess || detail.Value is null)
                {
                    return _Failure(detail.Error);
                }

                EsiKillmail killmail = detail.Value;
                if (killmail.Victim.CharacterId != characterId
                    && killmail.Attackers.All(attacker => attacker.CharacterId != characterId))
                {
                    notOnMail.Add(killmailId);
                    continue;
                }

                LocalKillmail entity = _ToEntity(characterId, hash, killmail);
                entity.SharedFromFleetId = fleetId;
                entity.SharedFromServer = serverIdentity;
                fetched.Add(entity);
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            Result stored = await dispatcher.Send(new ReconcileFleetKillmailShareCommand(characterId, serverIdentity, fleetId,
                [.. shared.Select(entry => entry.KillmailId).Where(id => !notOnMail.Contains(id))], fetched), cancellationToken);
            if (!stored.IsSuccess)
            {
                return new KillmailImportResult(KillmailImportStatus.Failed, 0,
                    stored.Messages.FirstOrDefault()?.Text ?? "Storing the shared killmails failed.");
            }

            // The mate's group run is synced to this client, so their loss links to it the same way an own loss does.
            Result<int> linked = await dispatcher.Send(new LinkKillmailsToRunsCommand(characterId), cancellationToken);
            return linked.IsSuccess
                ? KillmailImportResult.Ok(fetched.Count)
                : new KillmailImportResult(KillmailImportStatus.Failed, fetched.Count,
                    linked.Messages.FirstOrDefault()?.Text ?? "Linking losses to their runs failed.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Fills in the victim position of mails stored before it was kept (ET-473), re-reading each from the public
    /// <c>/killmails/{id}/{hash}/</c> — immutable and cached, so a mail is asked for once. One at a time, never in
    /// parallel. Stops at the first failed read and stores what it has; the rest follow on the next refresh.
    /// </summary>
    public async Task BackfillPositionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(int KillmailId, string Hash)> missing = await killmails.GetWithoutPositionAsync(cancellationToken);
        Dictionary<int, KillmailPosition> positions = [];
        foreach ((int killmailId, string hash) in missing)
        {
            var detail = await esi.GetAsync<EsiKillmail>($"/killmails/{killmailId}/{hash}/", cancellationToken: cancellationToken);
            // ponytail: a mail that fails for good holds back the ones after it; skip-and-log it if that ever happens.
            if (!detail.IsSuccess || detail.Value is null)
            {
                break;
            }

            if (detail.Value.Victim.Position is { } position)
            {
                positions[killmailId] = new KillmailPosition(position.X, position.Y, position.Z);
            }
        }

        if (positions.Count == 0)
        {
            return;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetKillmailPositionsCommand(positions), cancellationToken);
    }

    /// <summary>
    /// Fills in the attacker security status of mails stored before it was kept (ET-477), re-reading each from the public
    /// <c>/killmails/{id}/{hash}/</c> — immutable, so a mail with every player attacker filled is never asked for again.
    /// At most <see cref="SecurityStatusBatchSize"/> mails per call, one at a time, so a large library drains over several
    /// refreshes instead of one burst; stops at the first failed read (the ESI client has already backed off) and stores
    /// what it has. Returns how many mails were filled.
    /// </summary>
    public async Task<int> BackfillSecurityStatusesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(int KillmailId, string Hash)> missing =
            await killmails.GetWithoutAttackerSecurityStatusAsync(SecurityStatusBatchSize, cancellationToken);
        Dictionary<int, IReadOnlyList<KillmailAttackerSecurityStatus>> statuses = [];
        foreach ((int killmailId, string hash) in missing)
        {
            var detail = await esi.GetAsync<EsiKillmail>($"/killmails/{killmailId}/{hash}/", cancellationToken: cancellationToken);
            if (!detail.IsSuccess || detail.Value is null)
            {
                break;
            }

            List<KillmailAttackerSecurityStatus> read = [.. detail.Value.Attackers
                .Select((attacker, ordinal) => (attacker.SecurityStatus, ordinal))
                .Where(entry => entry.SecurityStatus is not null)
                .Select(entry => new KillmailAttackerSecurityStatus(entry.ordinal, entry.SecurityStatus.GetValueOrDefault()))];
            if (read.Count > 0)
            {
                statuses[killmailId] = read;
            }
        }

        if (statuses.Count == 0)
        {
            return 0;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        Result stored = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetKillmailAttackerSecurityStatusesCommand(statuses), cancellationToken);
        return stored.IsSuccess ? statuses.Count : 0;
    }

    // Shared by ImportAsync and ImportOneAsync (ET-374): store, replace any matching provisional row (ET-340), then
    // run the ET-331 link pass for the same character, so a pasted link joins a run exactly like the feed does.
    private async Task<KillmailImportResult> _StoreAndLinkAsync(IDispatcher dispatcher, int characterId,
        IReadOnlyList<LocalKillmail> killmails, int alreadyStored, CancellationToken cancellationToken)
    {
        Result stored = await dispatcher.Send(new StoreKillmailsCommand(characterId, killmails), cancellationToken);
        if (!stored.IsSuccess)
        {
            return new KillmailImportResult(KillmailImportStatus.Failed, alreadyStored,
                stored.Messages.FirstOrDefault()?.Text ?? "Storing the killmails failed.");
        }

        if (killmails.Count > 0)
        {
            await _ReplaceProvisionalAsync(characterId, killmails, cancellationToken);
        }

        // Also without new mails: a loss nothing fitted before may fit a run stopped or fitted since.
        Result<int> linked = await dispatcher.Send(new LinkKillmailsToRunsCommand(characterId), cancellationToken);
        int total = alreadyStored + killmails.Count;
        return linked.IsSuccess
            ? KillmailImportResult.Ok(total)
            : new KillmailImportResult(KillmailImportStatus.Failed, total,
                linked.Messages.FirstOrDefault()?.Text ?? "Linking losses to their runs failed.");
    }

    // ET-340: matches a provisional row on time/ship/victim. Victim name resolves locally only — character
    // registry, then the ET-336 name cache — never a fresh ESI call.
    // ponytail: unresolved name leaves the row standing (ticket's own accepted ceiling).
    private async Task _ReplaceProvisionalAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IReadOnlyList<Character> characters = await scope.ServiceProvider.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken);
        IKillmailEntityNameRepository names = scope.ServiceProvider.GetRequiredService<IKillmailEntityNameRepository>();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        List<int> victimIds = [.. killmails.Select(killmail => killmail.VictimCharacterId).OfType<int>().Distinct()];
        IReadOnlyDictionary<long, KillmailEntityName> cachedNames = victimIds.Count > 0
            ? await names.GetManyAsync([.. victimIds.Select(id => (long)id)], cancellationToken)
            : new Dictionary<long, KillmailEntityName>();

        foreach (LocalKillmail killmail in killmails)
        {
            if (killmail.VictimCharacterId is not { } victimId)
            {
                continue;
            }

            string? victimName = characters.FirstOrDefault(character => character.EsiCharacterId == victimId)?.Name
                ?? (cachedNames.TryGetValue(victimId, out KillmailEntityName? cached) ? cached.Name : null);
            if (victimName is null)
            {
                continue;
            }

            await dispatcher.Send(
                new RemoveMatchingProvisionalKillmailCommand(characterId, killmail.KillmailTimeUtc, killmail.VictimShipTypeId, victimName),
                cancellationToken);
        }
    }

    private static LocalKillmail _ToEntity(int characterId, string hash, EsiKillmail killmail) => new()
    {
        CharacterId = characterId,
        KillmailId = killmail.KillmailId,
        Hash = hash,
        KillmailTimeUtc = killmail.KillmailTime.UtcDateTime,
        SolarSystemId = killmail.SolarSystemId,
        IsLoss = killmail.Victim.CharacterId == characterId,
        VictimShipTypeId = killmail.Victim.ShipTypeId,
        VictimCharacterId = killmail.Victim.CharacterId,
        VictimCorporationId = killmail.Victim.CorporationId,
        VictimAllianceId = killmail.Victim.AllianceId,
        DamageTaken = killmail.Victim.DamageTaken,
        PositionX = killmail.Victim.Position?.X,
        PositionY = killmail.Victim.Position?.Y,
        PositionZ = killmail.Victim.Position?.Z,
        LinkSource = KillmailLinkSource.None,
        ImportedAtUtc = DateTime.UtcNow,
        Items = _ToItems(characterId, killmail),
        Attackers = killmail.Attackers.Select((attacker, ordinal) => new LocalKillmailAttacker
        {
            CharacterId = characterId,
            KillmailId = killmail.KillmailId,
            Ordinal = ordinal,
            AttackerCharacterId = attacker.CharacterId,
            CorporationId = attacker.CorporationId,
            AllianceId = attacker.AllianceId,
            FactionId = attacker.FactionId,
            ShipTypeId = attacker.ShipTypeId,
            WeaponTypeId = attacker.WeaponTypeId,
            DamageDone = attacker.DamageDone,
            FinalBlow = attacker.FinalBlow,
            SecurityStatus = attacker.SecurityStatus
        }).ToList()
    };

    // A container's contents take its flag and IsNested, so they never merge with the same cargo lying loose;
    // stacks sharing (flag, type, nested) are summed rather than the last one winning.
    private static List<LocalKillmailItem> _ToItems(int characterId, EsiKillmail killmail) => killmail.Victim.Items
        .SelectMany(item => item.Items
            .Select(nested => (item.Flag, Stack: nested, IsNested: true))
            .Prepend((item.Flag, Stack: item, IsNested: false)))
        .GroupBy(line => (line.Flag, line.Stack.ItemTypeId, line.IsNested))
        .Select(group => new LocalKillmailItem
        {
            CharacterId = characterId,
            KillmailId = killmail.KillmailId,
            Flag = group.Key.Flag,
            TypeId = group.Key.ItemTypeId,
            IsNested = group.Key.IsNested,
            QuantityDestroyed = group.Sum(line => line.Stack.QuantityDestroyed ?? 0),
            QuantityDropped = group.Sum(line => line.Stack.QuantityDropped ?? 0)
        })
        .ToList();

    private static KillmailImportResult _Failure(EsiError? error) => error?.Kind switch
    {
        EsiErrorKind.ScopeMissing => new KillmailImportResult(KillmailImportStatus.ScopeMissing, 0,
            "Killmail scope not granted — re-authorize the character to import killmails."),
        EsiErrorKind.AuthRequired => new KillmailImportResult(KillmailImportStatus.AuthRequired, 0,
            "Character must re-authenticate."),
        _ => new KillmailImportResult(KillmailImportStatus.Failed, 0, error?.Message ?? "Killmail import failed.")
    };
}
