using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Repositories;

namespace EveUtils.Client.Killmails;

/// <summary>
/// Imports a character's kills and losses: walks <c>/characters/{id}/killmails/recent/</c> until a page holds only
/// known ids, then adds each new mail from <c>/killmails/{id}/{hash}/</c> with its items and attackers, ids only.
/// </summary>
public sealed class EsiKillmailImporter(IEsiClient esi, ILocalKillmailRepository repository)
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

                var known = await repository.GetKnownIdsAsync(characterId,
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
            var killmails = new List<LocalKillmail>();
            foreach (var entry in newRefs.DistinctBy(entry => entry.KillmailId))
            {
                var detail = await esi.GetAsync<EsiKillmail>($"/killmails/{entry.KillmailId}/{entry.KillmailHash}/",
                    cancellationToken: cancellationToken);
                if (!detail.IsSuccess || detail.Value is null)
                {
                    return _Failure(detail.Error);
                }

                killmails.Add(_ToEntity(characterId, entry.KillmailHash, detail.Value));
            }

            await repository.AddMissingAsync(characterId, killmails, cancellationToken);
            return KillmailImportResult.Ok(killmails.Count);
        }
        finally
        {
            gate.Release();
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
