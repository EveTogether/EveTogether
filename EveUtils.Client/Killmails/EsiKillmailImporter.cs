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
            Result stored = await dispatcher.Send(new StoreKillmailsCommand(characterId, fetched), cancellationToken);
            if (!stored.IsSuccess)
            {
                return new KillmailImportResult(KillmailImportStatus.Failed, 0,
                    stored.Messages.FirstOrDefault()?.Text ?? "Storing the killmails failed.");
            }

            if (fetched.Count > 0)
            {
                await _ReplaceProvisionalAsync(characterId, fetched, cancellationToken);
            }

            // Also without new mails: a loss nothing fitted before may fit a run stopped or fitted since.
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
    /// Imports one killmail by id + hash — a pasted ESI link or in-game <c>killReport:</c> link (ET-338), bypassing
    /// the 5-minute cache on <c>/characters/{id}/killmails/recent/</c>. Stored for every own character on the mail,
    /// victim or attacker; refused with nothing stored when none of them are. <see cref="StoreKillmailsCommand"/>
    /// already skips a mail already known for that character, so a mail the feed later re-discovers (or already found
    /// first) never duplicates.
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

        var stored = 0;
        foreach (int characterId in ownCharacterIds)
        {
            SemaphoreSlim gate = _importGates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            LocalKillmail entity = _ToEntity(characterId, hash, killmail);
            try
            {
                Result result = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                    .Send(new StoreKillmailsCommand(characterId, [entity]), cancellationToken);
                if (!result.IsSuccess)
                {
                    return new KillmailImportResult(KillmailImportStatus.Failed, stored,
                        result.Messages.FirstOrDefault()?.Text ?? "Storing the killmail failed.");
                }

                stored++;
            }
            finally
            {
                gate.Release();
            }

            await _ReplaceProvisionalAsync(characterId, [entity], cancellationToken);
        }

        return KillmailImportResult.Ok(stored);
    }

    // ET-340: matches a provisional row on time/ship/victim. Victim name resolves locally only — character
    // registry, then the ET-336 name cache — never a fresh ESI call.
    // ponytail: unresolved name leaves the row standing (ticket's own accepted ceiling).
    private async Task _ReplaceProvisionalAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IReadOnlyList<Character> characters = await scope.ServiceProvider.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken);
        IKillmailEntityNameRepository names = scope.ServiceProvider.GetRequiredService<IKillmailEntityNameRepository>();
        IProvisionalKillmailRepository provisional = scope.ServiceProvider.GetRequiredService<IProvisionalKillmailRepository>();

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

            await provisional.RemoveMatchingAsync(characterId, killmail.KillmailTimeUtc, killmail.VictimShipTypeId, victimName, cancellationToken);
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
            FinalBlow = attacker.FinalBlow
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
