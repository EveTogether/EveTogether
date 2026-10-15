using System.Globalization;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Telemetry;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Runs;

/// <summary>Maps ET's <see cref="RunWirePayload"/> onto EVE Workbench's own import contract (ET-325, EWB-187's
/// <c>ImportedRun</c>). Only what the contract names travels: nothing of another pilot, no killmails, no e-war.</summary>
public static class EveWorkbenchRunImport
{
    public const string Source = "EVE Together";

    /// <summary>The run in EVE Workbench's shape, and how many mining lines were left out because the SDE does not
    /// know their ore's type id.</summary>
    public static (EveWorkbenchImportedRun Run, int SkippedOreLines) From(RunWirePayload payload, ISdeAccessor? sde)
    {
        RunWireData run = payload.Run;
        bool inGroup = run.GroupCode is not null && run.IsParticipant && run.DeletedAtUtc is null;
        var ores = run.MiningEntries
            .Select(entry => (Entry: entry, TypeId: sde is { IsAvailable: true } && sde.TryGetTypeId(entry.OreType, out int typeId) ? typeId : 0))
            .ToList();
        (int Tier, string Weather)? filament = run.Parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue.Split('|') is [var tierText, var weatherText]
            && int.TryParse(tierText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier) ? (tier, weatherText) : null;

        var imported = new EveWorkbenchImportedRun
        {
            ExternalId = run.Id,
            Revision = run.Revision,
            CharacterId = run.CharacterId,
            Kind = run.SiteTypeSource == SiteTypeSource.Mission ? EveWorkbenchRunKind.Mission : Enum.Parse<EveWorkbenchRunKind>(run.ActivityKind.ToString()),
            DungeonId = run.SiteTypeSource == SiteTypeSource.Uncatalogued || run.SiteTypeId <= 0 ? null : run.SiteTypeId,
            SolarSystemId = run.SolarSystemId,
            StartedAtUtc = _Utc(run.StartedAtUtc),
            StoppedAtUtc = _Utc(run.StoppedAtUtc ?? run.StartedAtUtc),
            SentAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(payload.SentAtUnixMilliseconds).UtcDateTime,
            Deleted = run.DeletedAtUtc is not null,
            AbyssTier = filament is { Tier: >= 0 and <= 6 } ? filament.Value.Tier : null,
            AbyssWeather = Enum.TryParse(filament?.Weather, true, out EveWorkbenchAbyssWeather weather) && Enum.IsDefined(weather) ? weather : null,
            IskBounties = run.BountyEntries.Sum(entry => entry.Isk),
            IskRewards = _Sum(run, RunParameterKey.Isk, RunParameterKey.FixedPayout, RunParameterKey.Escrow),
            IskBonusRewards = _Sum(run, RunParameterKey.BonusIsk),
            LpRewards = (int)_Sum(run, RunParameterKey.LoyaltyPoints),
            EvermarkRewards = (int)_Sum(run, RunParameterKey.Evermarks),
            Group = inGroup ? new EveWorkbenchImportedRunGroup
            {
                Code = run.GroupCode!,
                IsOrganiser = run.Role == RunRole.FleetCommander,
                IsPayoutEligible = run.IsPayoutEligible,
                ParticipantCount = run.AttendanceCount ?? run.FleetSizeAtStop,
                Outcome = run.HomefrontOutcome is { } outcome ? Enum.Parse<EveWorkbenchRunGroupOutcome>(outcome.ToString()) : null
            } : null,
            Loot =
            [
                .. run.LootCaptures.Where(capture => !capture.IsExcluded).SelectMany(capture => capture.Entries.Select(entry => new EveWorkbenchImportedLoot
                {
                    TypeId = entry.ItemTypeId, Name = entry.Name, Quantity = entry.Quantity ?? 1, Lost = entry.LootKind == LootKind.Lost,
                    CapturedAtUtc = _Utc(capture.CapturedAtUtc)
                })),
                .. ores.Where(ore => ore.TypeId > 0).Select(ore => new EveWorkbenchImportedLoot
                {
                    TypeId = ore.TypeId, Name = ore.Entry.OreType, Quantity = ore.Entry.Units, CapturedAtUtc = _Utc(ore.Entry.LastObservedAtUtc)
                })
            ],
            RoomStartsUtc = [.. RunRooms.Boundaries(run.Parameters.Select(parameter => new RunParameterDto(run.Id, parameter.ParameterKey,
                parameter.TypedValue, parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc)), run.Id).Select(_Utc)],
            Enemies = [.. run.EnemyObservations.Select(enemy => new EveWorkbenchImportedEnemy
            {
                TypeId = enemy.EnemyTypeId, Name = enemy.EnemyName, Count = enemy.Count, RoomNumber = enemy.RoomNumber,
                FirstSeenAtUtc = _Utc(enemy.FirstObservedAtUtc), LastSeenAtUtc = _Utc(enemy.LastObservedAtUtc)
            })],
            CombatWithheld = run.CombatWithheld,
            Combat = run.CombatWithheld || run.CombatTimeline is not { } combat ? null : new EveWorkbenchImportedCombat
            {
                Seconds = combat.Seconds,
                MaxHitOut = combat.MaxHitOut,
                MaxHitOutTarget = _NoPilot(combat.MaxHitOutTarget),
                MaxHitIn = combat.MaxHitIn,
                MaxHitInSource = _NoPilot(combat.MaxHitInSource),
                HitsOut = combat.HitsOut,
                MissesOut = combat.MissesOut,
                HitsIn = combat.HitsIn,
                MissesIn = combat.MissesIn,
                Series = [.. combat.Series.Select(series => new EveWorkbenchImportedCombatSeries
                {
                    Kind = Enum.Parse<EveWorkbenchCombatSeriesKind>(series.Kind.ToString()),
                    PerSecond = RunCombatTelemetry.Decode(series.Samples, combat.Seconds)
                })],
                HitTallies = [.. combat.HitTallies.Select(tally => new EveWorkbenchImportedHitTally
                {
                    Direction = tally.Direction == DamageDirection.Outgoing ? EveWorkbenchHitDirection.Out : EveWorkbenchHitDirection.In,
                    Counterparty = _NoPilot(tally.Counterparty)!, Weapon = tally.Weapon,
                    Quality = Enum.Parse<EveWorkbenchHitQuality>(tally.Quality.ToString()),
                    Count = tally.Count, Sum = tally.Sum, Min = tally.Min, Max = tally.Max
                })]
            }
        };
        return (imported, ores.Count(ore => ore.TypeId <= 0));
    }

    private static decimal _Sum(RunWireData run, params RunParameterKey[] keys) => run.Parameters.Where(parameter => keys.Contains(parameter.ParameterKey))
        .Sum(parameter => parameter.Amount ?? (decimal.TryParse(parameter.TypedValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ? value : 0));

    private static DateTime _Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // A player in a combat line carries corp/alliance tickers in brackets; an NPC never does.
    private static string? _NoPilot(string? counterparty) => counterparty?.Contains('[') == true ? "Pilot" : counterparty;
}

public sealed class EveWorkbenchRunImportRequest
{
    public string Source { get; init; } = EveWorkbenchRunImport.Source;
    public required IReadOnlyList<EveWorkbenchImportedRun> Runs { get; init; }
}

public sealed class EveWorkbenchImportedRun
{
    public Guid ExternalId { get; init; }
    public int Revision { get; init; }
    public long CharacterId { get; init; }
    public EveWorkbenchRunKind Kind { get; init; }
    public int? DungeonId { get; init; }
    public int? SolarSystemId { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime StoppedAtUtc { get; init; }
    public DateTime? SentAtUtc { get; init; }
    public bool Deleted { get; init; }
    public int? AbyssTier { get; init; }
    public EveWorkbenchAbyssWeather? AbyssWeather { get; init; }
    public decimal IskBounties { get; init; }
    public decimal IskRewards { get; init; }
    public decimal IskBonusRewards { get; init; }
    public int LpRewards { get; init; }
    public int EvermarkRewards { get; init; }
    public EveWorkbenchImportedRunGroup? Group { get; init; }
    public IReadOnlyList<EveWorkbenchImportedLoot> Loot { get; init; } = [];
    public IReadOnlyList<DateTime> RoomStartsUtc { get; init; } = [];
    public IReadOnlyList<EveWorkbenchImportedEnemy> Enemies { get; init; } = [];
    public bool CombatWithheld { get; init; }
    public EveWorkbenchImportedCombat? Combat { get; init; }
}

public sealed class EveWorkbenchImportedRunGroup
{
    public string Code { get; init; } = string.Empty;
    public bool IsOrganiser { get; init; }
    public bool IsPayoutEligible { get; init; }
    public int? ParticipantCount { get; init; }
    public EveWorkbenchRunGroupOutcome? Outcome { get; init; }
}

public sealed class EveWorkbenchImportedLoot
{
    public int TypeId { get; init; }
    public string? Name { get; init; }
    public long Quantity { get; init; }
    public bool Lost { get; init; }
    public DateTime? CapturedAtUtc { get; init; }
}

public sealed class EveWorkbenchImportedEnemy
{
    public int TypeId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
    public int? RoomNumber { get; init; }
    public DateTime FirstSeenAtUtc { get; init; }
    public DateTime LastSeenAtUtc { get; init; }
}

public sealed class EveWorkbenchImportedCombat
{
    public int Seconds { get; init; }
    public int MaxHitOut { get; init; }
    public string? MaxHitOutTarget { get; init; }
    public int MaxHitIn { get; init; }
    public string? MaxHitInSource { get; init; }
    public int HitsOut { get; init; }
    public int MissesOut { get; init; }
    public int HitsIn { get; init; }
    public int MissesIn { get; init; }
    public IReadOnlyList<EveWorkbenchImportedCombatSeries> Series { get; init; } = [];
    public IReadOnlyList<EveWorkbenchImportedHitTally> HitTallies { get; init; } = [];
}

public sealed class EveWorkbenchImportedCombatSeries
{
    public EveWorkbenchCombatSeriesKind Kind { get; init; }
    public int[] PerSecond { get; init; } = [];
}

public sealed class EveWorkbenchImportedHitTally
{
    public EveWorkbenchHitDirection Direction { get; init; }
    public string Counterparty { get; init; } = string.Empty;
    public string? Weapon { get; init; }
    public EveWorkbenchHitQuality Quality { get; init; }
    public int Count { get; init; }
    public long Sum { get; init; }
    public int Min { get; init; }
    public int Max { get; init; }
}

// Numeric on the wire, in EVE Workbench's order.
public enum EveWorkbenchRunKind { Abyssal, Site, Mission, Mining }
public enum EveWorkbenchAbyssWeather { Electrical, Dark, Exotic, Firestorm, Gamma }
public enum EveWorkbenchRunGroupOutcome { Completed, Failed, Unknown }
public enum EveWorkbenchCombatSeriesKind { DmgOut, DmgIn, RepOut, RepIn, NeutOut, NeutIn, CapOut, CapIn }
public enum EveWorkbenchHitDirection { Out, In }
public enum EveWorkbenchHitQuality { Misses, Glances, Hits, Grazes, Penetrates, Smashes, Wrecks }
