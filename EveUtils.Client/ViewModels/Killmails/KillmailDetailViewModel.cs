using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.Killmails;
using EveUtils.Client.Imaging;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Queries;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>
/// The detail screen of one killmail (ET-333): victim, fit split into destroyed/dropped, attackers and value — a
/// hosted module like ACTIVITY and FIT DETAIL, opened via <see cref="IDialogService.ShowKillmailDetail"/> with module
/// id <c>killmail-{characterId}-{killmailId}</c>, so the same mail opened from two places is one tab (AC7).
///
/// <para>Reads nothing from ESI itself (AC8): the query, the SDE lookups and <see cref="KillmailNames.HydrateAsync"/>
/// are the only sources, and the last one only ever asks for a player name that is missing or stale.</para>
/// </summary>
public sealed partial class KillmailDetailViewModel : ViewModelBase
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IServiceProvider _services;
    private readonly ISdeAccessor _sde;
    private readonly int _characterId;
    private readonly int _killmailId;

    public KillmailDetailViewModel(CqrsDispatcher dispatcher, IDialogService dialogs, IServiceProvider services,
        int characterId, int killmailId)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _services = services;
        _sde = services.GetRequiredService<ISdeAccessor>();
        _characterId = characterId;
        _killmailId = killmailId;
        ModuleId = $"killmail-{characterId}-{killmailId}";
    }

    public string ModuleId { get; }

    [ObservableProperty] private string _title = "KILLMAIL";
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty] private string _shipName = string.Empty;
    [ObservableProperty] private Bitmap? _shipImage;
    [ObservableProperty] private string _kindGlyph = "▼";
    [ObservableProperty] private bool _isLoss;
    [ObservableProperty] private string _kindText = string.Empty;
    [ObservableProperty] private bool _isFailedLoss;
    [ObservableProperty] private string _pilotName = string.Empty;
    [ObservableProperty] private string _corpAllianceText = string.Empty;
    [ObservableProperty] private string _timeText = string.Empty;
    [ObservableProperty] private string _systemLineText = string.Empty;
    [ObservableProperty] private bool _isAbyssal;

    [ObservableProperty] private string _primaryFigureLabel = string.Empty;
    [ObservableProperty] private string _primaryFigureText = string.Empty;
    [ObservableProperty] private string _destroyedText = string.Empty;
    [ObservableProperty] private string _droppedText = string.Empty;
    [ObservableProperty] private string _damageTakenText = string.Empty;
    [ObservableProperty] private string _fifthFigureLabel = string.Empty;
    [ObservableProperty] private string _fifthFigureText = string.Empty;

    [ObservableProperty] private bool _hasLinkedRun;
    [ObservableProperty] private string _linkedRunSummaryText = "kills are not linked to runs";
    [ObservableProperty] private KillmailLinkedRunViewModel? _linkedRun;

    [ObservableProperty] private string _fitHeaderText = string.Empty;
    [ObservableProperty] private string _fitSummaryText = string.Empty;

    public ObservableCollection<KillmailFitGroupViewModel> FitGroups { get; } = [];

    [ObservableProperty] private string _attackersSummaryText = string.Empty;

    public ObservableCollection<KillmailDetailAttackerRowViewModel> Attackers { get; } = [];

    [ObservableProperty] private string _footerText = string.Empty;

    private KillmailDetailDto? _detail;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Character> characters =
            await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync(cancellationToken);
        Dictionary<int, string> ownCharacterNames = [];
        foreach (Character character in characters)
        {
            if (character.EsiCharacterId is { } id && id > 0 && !ownCharacterNames.ContainsKey(id))
            {
                ownCharacterNames[id] = character.Name;
            }
        }
        var names = new KillmailNames(ownCharacterNames, _services.GetService<IExternalCharacterLookup>(),
            _services.GetRequiredService<IEsiAffiliationResolver>(), _sde,
            _services.GetRequiredService<IKillmailEntityNameRepository>(), _services.GetRequiredService<ISettingRepository>(),
            _services.GetService<TimeProvider>() ?? TimeProvider.System);

        Result<KillmailDetailDto> result =
            await _dispatcher.Query(new GetKillmailDetailQuery(_characterId, _killmailId), cancellationToken);
        if (!result.IsSuccess || result.Value is not { } detail)
        {
            StatusMessage = result.Messages.Count > 0 ? result.Messages[0].Text : "This killmail could not be read.";
            return;
        }

        int[] characterIds = [.. new[] { detail.VictimCharacterId }.Concat(detail.Attackers.Select(attacker => attacker.CharacterId)).OfType<int>()];
        int[] corporationIds = [.. new[] { detail.VictimCorporationId }.Concat(detail.Attackers.Select(attacker => attacker.CorporationId)).OfType<int>()];
        int[] allianceIds = [.. new[] { detail.VictimAllianceId }.Concat(detail.Attackers.Select(attacker => attacker.AllianceId)).OfType<int>()];
        await names.HydrateAsync(characterIds, corporationIds, allianceIds, cancellationToken);

        _detail = detail;
        HashSet<long> ownCharacterIds = [.. ownCharacterNames.Keys.Select(id => (long)id)];
        _Apply(detail, names, ownCharacterIds);
        _ = _LoadImagesAsync();
    }

    private async Task _LoadImagesAsync()
    {
        ITypeImageProvider? images = _services.GetService<ITypeImageProvider>();
        if (images is null || !await images.AreImagesEnabledAsync())
        {
            return;
        }

        if (_detail is { } detail)
        {
            ShipImage = await images.GetImageAsync(detail.VictimShipTypeId, TypeImageKind.Render, 128);
        }

        ICharacterPortraitProvider? portraits = _services.GetService<ICharacterPortraitProvider>();
        if (portraits is not null)
        {
            await Task.WhenAll(Attackers.Select(attacker => attacker.LoadImageAsync(images, portraits)));
        }

        await Task.WhenAll(FitGroups.SelectMany(group => group.Rows).Select(row => row.LoadIconAsync(images)));
    }

    private void _Apply(KillmailDetailDto detail, KillmailNames names, IReadOnlySet<long> ownCharacterIds)
    {
        SdeType? shipType = _sde.GetType(detail.VictimShipTypeId);
        ShipName = shipType?.Name ?? $"type {detail.VictimShipTypeId}";
        string shipClass = shipType is not null ? _sde.GetGroup(shipType.GroupId)?.Name ?? string.Empty : string.Empty;
        Title = ShipName.ToUpperInvariant();

        IsLoss = detail.IsLoss;
        KindGlyph = detail.IsLoss ? "▼" : "▲";
        IsFailedLoss = detail.IsLoss && detail.LinkedRun is { ActivityKind: ActivityKind.Abyssal };
        KindText = $"{(detail.IsLoss ? "LOSS" : "KILL")} · {shipClass}".ToUpperInvariant().TrimEnd(' ', '·');

        PilotName = detail.VictimCharacterId is { } victimCharacterId ? names.NameOf(victimCharacterId) : "Unknown pilot";
        CorpAllianceText = _CorpAllianceText(names, detail.VictimCorporationId, detail.VictimAllianceId);

        TimeText = detail.KillmailTimeUtc.ToLocalTime().ToString("ddd d MMM yyyy · HH:mm:ss", CultureInfo.InvariantCulture).ToUpperInvariant();
        IsAbyssal = AbyssalSpace.IsAbyssalSystem(detail.SolarSystemId);
        SystemLineText = _SystemLineText(detail.SolarSystemId, IsAbyssal);

        decimal? destroyedValue = _SumKnown([detail.ShipValue, .. detail.Items.Where(item => item.IsDestroyed).Select(item => item.Value)]);
        decimal? droppedValue = _SumKnown(detail.Items.Where(item => !item.IsDestroyed).Select(item => item.Value));
        decimal totalValue = destroyedValue.GetValueOrDefault() + droppedValue.GetValueOrDefault();

        PrimaryFigureLabel = detail.IsLoss ? "ISK LOST" : "ISK DESTROYED";
        PrimaryFigureText = destroyedValue is null && droppedValue is null ? "no price" : IskFormat.Compact(totalValue);
        DestroyedText = destroyedValue is { } d ? IskFormat.Compact(d) : "no price"; // the ship always counts, so
        // null here only ever means nothing at all is priced yet — never "nothing was destroyed".
        bool hasDroppedLines = detail.Items.Any(item => !item.IsDestroyed);
        DroppedText = droppedValue is { } dropped ? IskFormat.Compact(dropped) : hasDroppedLines ? "no price" : "0";
        DamageTakenText = detail.DamageTaken.ToString("N0", CultureInfo.InvariantCulture);

        if (detail.IsLoss)
        {
            FifthFigureLabel = "ATTACKERS";
            FifthFigureText = detail.Attackers.Count.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            FifthFigureLabel = "YOUR DAMAGE";
            KillmailDetailAttackerLineDto? own = detail.Attackers.FirstOrDefault(attacker => attacker.CharacterId == _characterId);
            int totalDamage = detail.Attackers.Sum(attacker => attacker.DamageDone);
            FifthFigureText = own is null
                ? "—"
                : $"{own.DamageDone:N0} · {(totalDamage > 0 ? own.DamageDone * 100.0 / totalDamage : 0):0}%";
        }

        _ApplyLinkedRun(detail);
        _ApplyFit(detail);
        _ApplyAttackers(detail, names, ownCharacterIds);

        FooterText = $"Killmail {detail.KillmailId:N0} · read {DateTime.Now:HH:mm}";
    }

    private void _ApplyLinkedRun(KillmailDetailDto detail)
    {
        HasLinkedRun = detail.LinkedRun is not null;
        if (detail.LinkedRun is not { } linkedRun)
        {
            LinkedRunSummaryText = detail.IsLoss ? "not linked to a run" : "kills are not linked to runs";
            LinkedRun = null;
            return;
        }

        bool isFailed = linkedRun.ActivityKind == ActivityKind.Abyssal;
        LinkedRunSummaryText = isFailed ? "FAILED" : "SHIP LOST";

        IReadOnlyList<LinkedLossRunChoice> otherRuns = [.. linkedRun.OtherRuns.Select(other =>
            new LinkedLossRunChoice(other.RunId, $"{other.SiteName ?? "unnamed run"} · {other.StartedAtUtc.ToLocalTime():d MMM HH:mm}"))];

        DateOnly day = DateOnly.FromDateTime(linkedRun.StartedAtUtc.ToLocalTime());
        LinkedRun = new KillmailLinkedRunViewModel(_dispatcher, detail.CharacterId, detail.KillmailId,
            linkedRun.ActivitySummaryId, day, otherRuns, _OpenRunAsync, () => LoadAsync())
        {
            SiteText = $"{linkedRun.SiteName ?? "unnamed run"} · {_sde.GetType(detail.VictimShipTypeId)?.Name ?? ShipName}",
            TimeText = linkedRun.StartedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            IsFailed = isFailed,
            StatusChipText = isFailed ? "FAILED" : "SHIP LOST",
            ValueText = PrimaryFigureText,
            ReasonText = _LinkReasonText(linkedRun.LinkSource, detail.VictimShipTypeId)
        };
    }

    private async Task _OpenRunAsync(Guid activitySummaryId, DateOnly day)
    {
        IReadOnlyList<Character> characters = await _services.GetRequiredService<ICharacterRegistry>().GetAllAsync();
        RunsOverviewViewModel shown = _dialogs.ShowRuns(new RunsOverviewViewModel(_dispatcher, _dialogs, _services, characters));
        // ShowRuns fires LoadAsync without awaiting it (fire-and-observe, like every other Show* call) — awaited here
        // instead, or OpenRunAsync would find no row yet and silently do nothing on a screen that was not already open.
        await shown.LoadAsync();
        await shown.OpenRunAsync(activitySummaryId, day);
    }

    private static string _LinkReasonText(KillmailLinkSource linkSource, int victimShipTypeId) => linkSource switch
    {
        KillmailLinkSource.Manual => "linked by hand",
        _ when KillmailRunLinker.IsCapsule(victimShipTypeId) => "the pod followed its ship within a minute",
        _ => "the only run of this pilot at that time, place and hull"
    };

    private void _ApplyFit(KillmailDetailDto detail)
    {
        FitHeaderText = $"{ShipName} · as fitted when {(detail.IsLoss ? "lost" : "destroyed")}";
        FitSummaryText = $"{detail.Items.Count} items · {DestroyedText} destroyed · {DroppedText} dropped";

        FitGroups.Clear();
        var shipLine = new KillmailDetailItemLineDto(0, detail.VictimShipTypeId, false, true, 1, detail.ShipValue);
        FitGroups.Add(new KillmailFitGroupViewModel("SHIP",
            detail.ShipValue is { } shipValue ? IskFormat.Compact(shipValue) : "no price",
            [new KillmailDetailItemRowViewModel(shipLine, ShipName, null, isTopValue: true)]));

        foreach (IGrouping<string, KillmailDetailItemLineDto> group in detail.Items
                     .GroupBy(_GroupKey)
                     .OrderBy(group => Array.IndexOf(_groupOrder, group.Key) is var index && index < 0 ? _groupOrder.Length : index))
        {
            List<KillmailDetailItemLineDto> lines = [.. group];
            decimal? topValue = lines.Select(line => line.Value).Max();
            List<KillmailDetailItemRowViewModel> rows = [.. lines.Select(line => new KillmailDetailItemRowViewModel(
                line, _sde.GetType(line.TypeId)?.Name ?? $"type {line.TypeId}", _MetaHint(line.Flag, line.TypeId),
                line.Value is not null && line.Value == topValue))];

            decimal? groupValue = _SumKnown(lines.Select(line => line.Value));
            FitGroups.Add(new KillmailFitGroupViewModel(group.Key,
                $"{lines.Count} · {(groupValue is { } value ? IskFormat.Compact(value) : "no price")}", rows));
        }
    }

    // "loaded" tells a charge apart from the module in the same slot (both share the flag) — the same
    // sde.GetSlotType(...) == None check FitDetailWindowViewModel.BuildSlots already uses for that split.
    private string? _MetaHint(int flag, int typeId)
    {
        if (KillmailFlags.NameOf(flag) is not { } flagName)
        {
            return null;
        }

        FitSlotCategory category = FitSlotClassifier.Classify(flagName);
        return category is FitSlotCategory.High or FitSlotCategory.Medium or FitSlotCategory.Low
               && _sde.GetSlotType(typeId) == SdeSlotType.None
            ? "loaded"
            : null;
    }

    private static readonly string[] _groupOrder =
    [
        "HIGH SLOTS", "MID SLOTS", "LOW SLOTS", "RIGS", "SUBSYSTEMS", "SERVICE SLOTS",
        "DRONE BAY", "FIGHTER BAY", "CARGO", "IMPLANTS/BOOSTERS", "OTHER"
    ];

    private static string _GroupKey(KillmailDetailItemLineDto line)
    {
        string? flagName = KillmailFlags.NameOf(line.Flag);
        if (flagName is null)
        {
            return "OTHER";
        }

        return flagName is "Implant" or "Booster" ? "IMPLANTS/BOOSTERS" : FitSlotClassifier.Label(FitSlotClassifier.Classify(flagName));
    }

    private void _ApplyAttackers(KillmailDetailDto detail, KillmailNames names, IReadOnlySet<long> ownCharacterIds)
    {
        Attackers.Clear();
        int totalDamage = detail.Attackers.Sum(attacker => attacker.DamageDone);
        KillmailDetailAttackerLineDto? finalBlow = detail.Attackers.FirstOrDefault(attacker => attacker.FinalBlow);
        int npcCount = detail.Attackers.Count(attacker => attacker.CharacterId is null);

        AttackersSummaryText = npcCount == detail.Attackers.Count
            ? $"{detail.Attackers.Count} NPCs · final blow {(finalBlow is not null ? _AttackerName(finalBlow, names) : "unknown")}"
            : $"{detail.Attackers.Count} · final blow {(finalBlow is not null ? _AttackerName(finalBlow, names) : "unknown")}";

        foreach (KillmailDetailAttackerLineDto attacker in detail.Attackers.OrderByDescending(attacker => attacker.DamageDone))
        {
            bool isNpc = attacker.CharacterId is null;
            string? weapon = attacker.CharacterId is not null && attacker.WeaponTypeId is { } weaponTypeId
                ? _sde.GetType(weaponTypeId)?.Name
                : null;
            Attackers.Add(new KillmailDetailAttackerRowViewModel(
                _AttackerName(attacker, names), _AttackerSubText(attacker, names),
                attacker.ShipTypeId is { } shipTypeId ? _sde.GetType(shipTypeId)?.Name ?? $"type {shipTypeId}" : "unknown ship",
                weapon, attacker.DamageDone, totalDamage > 0 ? attacker.DamageDone * 100.0 / totalDamage : 0,
                attacker.FinalBlow, attacker.TopDamage, ownCharacterIds.Contains(attacker.CharacterId ?? 0), isNpc,
                attacker.CharacterId, attacker.ShipTypeId, attacker.CorporationId));
        }
    }

    private string _AttackerName(KillmailDetailAttackerLineDto attacker, KillmailNames names) =>
        attacker.CharacterId is { } characterId ? names.NameOf(characterId)
        : attacker.ShipTypeId is { } shipTypeId ? _sde.GetType(shipTypeId)?.Name ?? $"type {shipTypeId}"
        : "Unknown";

    private string _AttackerSubText(KillmailDetailAttackerLineDto attacker, KillmailNames names)
    {
        if (attacker.CharacterId is not null)
        {
            return _CorpAllianceText(names, attacker.CorporationId, attacker.AllianceId);
        }

        if (attacker.FactionId is { } factionId)
        {
            return $"NPC · {_sde.GetFactionName(factionId) ?? $"faction {factionId}"}";
        }

        if (attacker.CorporationId is { } corporationId)
        {
            return $"NPC · {_sde.GetNpcCorporationName(corporationId) ?? names.NameOf(corporationId)}";
        }

        return "NPC";
    }

    private string _CorpAllianceText(KillmailNames names, int? corporationId, int? allianceId)
    {
        string corp = corporationId is { } id ? names.NameOf(id) : "unknown corporation";
        return allianceId is { } allianceIdValue ? $"{corp} · {names.NameOf(allianceIdValue)}" : $"{corp} · no alliance";
    }

    private string _SystemLineText(int solarSystemId, bool isAbyssal)
    {
        if (isAbyssal)
        {
            return $"Abyssal deadspace · {solarSystemId}";
        }

        SdeSolarSystem? system = _sde.GetSolarSystem(solarSystemId);
        return system is null
            ? $"system {solarSystemId}"
            : $"{system.Name} · {system.RegionName ?? "unknown region"} · {RunRowFacts.SecurityText(system.SecurityStatus)}";
    }

    private static decimal? _SumKnown(IEnumerable<decimal?> values)
    {
        List<decimal> known = [.. values.OfType<decimal>()];
        return known.Count == 0 ? null : known.Sum();
    }

    [RelayCommand]
    private async Task OpenFitAsync()
    {
        if (_detail is not { } detail)
        {
            return;
        }

        string name = $"{ShipName} — {(detail.IsLoss ? "lost" : "destroyed")} {detail.KillmailTimeUtc.ToLocalTime():d MMM HH:mm}";
        await FitDetailLauncher.OpenAsync(_services, _dialogs, KillmailFitBuilder.Build(_ToKillmail(detail), name), name);
    }

    // KillmailFitBuilder reads a LocalKillmail's Items — the DTO already carries the same (Flag, TypeId, quantity)
    // facts, split by destroyed/dropped, so they are recombined here rather than re-reading the database.
    private static LocalKillmail _ToKillmail(KillmailDetailDto detail)
    {
        var killmail = new LocalKillmail
        {
            CharacterId = detail.CharacterId, KillmailId = detail.KillmailId, Hash = detail.Hash,
            VictimShipTypeId = detail.VictimShipTypeId
        };
        foreach (var group in detail.Items.GroupBy(line => (line.Flag, line.TypeId, line.IsNested)))
        {
            killmail.Items.Add(new LocalKillmailItem
            {
                Flag = group.Key.Flag, TypeId = group.Key.TypeId, IsNested = group.Key.IsNested,
                QuantityDestroyed = group.Where(line => line.IsDestroyed).Sum(line => line.Quantity),
                QuantityDropped = group.Where(line => !line.IsDestroyed).Sum(line => line.Quantity)
            });
        }

        return killmail;
    }

    [RelayCommand]
    private async Task CopyEsiLinkAsync()
    {
        if (_detail is not { } detail)
        {
            return;
        }

        await _dialogs.SetClipboardTextAsync($"https://esi.evetech.net/killmails/{detail.KillmailId}/{detail.Hash}/");
    }
}
