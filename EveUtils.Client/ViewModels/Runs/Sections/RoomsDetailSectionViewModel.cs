using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// ROOMS on the detail screen (ET-469): per room the time, idle, damage, spawn HP, share, overkill and loot, on the one
/// room set the group's detail uses (ET-494), for the pilot picked in COMBAT. It reads rooms and never makes any.
/// </summary>
public sealed partial class RoomsDetailSectionViewModel : RunDetailSection
{
    private readonly CombatTelemetryChoice _choice;
    private readonly Shared.Modules.Sde.ISdeAccessor? _sde;
    private ActivityDetailDto? _detail;
    private bool _isSolo;

    public RoomsDetailSectionViewModel(RunDetailSectionServices services) : base(RunSectionId.Rooms, "ROOMS")
    {
        _sde = services.Sde;
        _choice = services.Combat ?? new CombatTelemetryChoice(services.Dispatcher, services.OwnCharacterIds);
        _choice.PropertyChanged += _OnChoiceChanged;
        HeaderSummary = NoRoomsText;
        RoomsEmptyText = NoRoomsText;
    }

    public ObservableCollection<RoomRowViewModel> Rows { get; } = [];

    [ObservableProperty] private RoomRowViewModel? _total;

    [ObservableProperty] private string? _roomsEmptyText;

    /// <summary>Only a counted room, solo or with every fleet mate's damage, has one; otherwise there is no column.</summary>
    [ObservableProperty] private bool _showOverkill;

    [ObservableProperty] private string? _overkillHint;

    public override bool HasContent => _detail is not null && RunRoomSet.Of(_detail) is not null;

    internal const string NoRoomsText =
        "No rooms: nobody pressed NEW ROOM and the game log showed no new room by itself.";

    public override void Apply(RunDetailSectionInput input)
    {
        _detail = input.Detail;
        _isSolo = input.Detail.Runs.Select(run => run.CharacterId).Distinct().Count() <= 1;
        _Rebuild();
    }

    private void _OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CombatTelemetryChoice.Shown))
        {
            _Rebuild();
        }
    }

    private void _Rebuild()
    {
        Rows.Clear();
        Total = null;
        ShowOverkill = false;
        OverkillHint = null;
        if (_detail is null || RunRoomSet.Of(_detail) is not { } set)
        {
            HeaderSummary = NoRoomsText;
            RoomsEmptyText = NoRoomsText;
            return;
        }

        RoomsEmptyText = null;
        RoomsTable table = RoomsTable.Of(_detail, set, _choice.ShownRunId, _choice.Timelines, typeId => _sde?.GetNpcRawHp(typeId));
        foreach (RoomRow row in table.Rows)
        {
            Rows.Add(RoomRowViewModel.Of(row, table.IsLootPerRoom) with { IsAlternate = Rows.Count % 2 == 1 });
        }

        Total = RoomRowViewModel.TotalOf(table);
        ShowOverkill = table.Rows.Any(row => row.Overkill is not null);
        if (!ShowOverkill && !_isSolo && table.CountedRooms > 0)
        {
            OverkillHint = "No overkill: it needs the damage of every pilot, and not every pilot shared theirs.";
        }

        HeaderSummary = $"{table.Rows.Count} rooms · {(table.IsByHand ? "by hand" : "auto")} · "
            + $"{table.CountedRooms} of {table.Rows.Count} counted";
    }

    public override string AbsentReason(string noun) => $"no ROOMS — {noun} has no rooms";
}

/// <summary>One line of the ROOMS table, already in the words the screen shows.</summary>
public sealed record RoomRowViewModel(string Title, string WindowText, string TimeText, string IdleText, string DamageText,
    string SpawnText, bool IsLowerBound, string SpawnTooltip, double ShareFraction, bool HasShare, string ShareText,
    string ShareTooltip, string OverkillText, string LootText, string LootTooltip)
{
    public bool IsAlternate { get; init; }

    internal static RoomRowViewModel Of(RoomRow row, bool isLootPerRoom) => new(
        $"ROOM {row.Number}",
        row.EndUtc is { } end
            ? $"{row.StartUtc.ToLocalTime():HH:mm:ss} – {end.ToLocalTime():HH:mm:ss}"
            : $"since {row.StartUtc.ToLocalTime():HH:mm:ss}",
        _Clock(row.DurationSeconds), row.IdleSeconds is { } idle ? _Clock(idle) : "—", _Number(row.DamageOut),
        _Spawn(row.SpawnHp, row.IsCounted), row.SpawnHp is not null && !row.IsCounted,
        row.SpawnHp is null ? "No enemy was seen in this room."
            : row.IsCounted ? "Σ count × raw HP (shield + armor + hull)"
            : "At least this: nobody counted every type here, so an uncounted type is taken as one.",
        Math.Min(1, row.Share ?? 0), row.Share is not null, row.Share is { } share ? Percent(share) : "—",
        row.Share is null ? "not counted" : "your damage ÷ spawn HP",
        row.Overkill is { } overkill ? Signed(overkill) : "—",
        !isLootPerRoom ? "—" : row.LootIsk is { } loot ? IskFormat.Whole(loot) : row.HasLootCapture ? "no price" : "—",
        !isLootPerRoom ? "Counted as a before/after difference, not per room." : "Gained loot copied in this room, at the run's fixed prices.");

    internal static RoomRowViewModel TotalOf(RoomsTable table)
    {
        IReadOnlyList<RoomRow> rows = table.Rows;
        bool isCounted = rows.All(row => row.IsCounted);
        long? spawn = rows.Any(row => row.SpawnHp is not null) ? rows.Sum(row => row.SpawnHp ?? 0) : null;
        long? damage = rows.All(row => row.DamageOut is not null) ? rows.Sum(row => row.DamageOut ?? 0) : null;
        double? share = isCounted && damage is not null && spawn > 0 ? (double)damage.Value / spawn : null;
        double? overkill = rows.All(row => row.Overkill is not null) && spawn > 0
            ? rows.Sum(row => (row.Overkill!.Value + 1) * row.SpawnHp!.Value) / spawn.Value - 1
            : null;
        bool lootKnown = table.IsLootPerRoom && rows.All(row => !row.HasLootCapture || row.LootIsk is not null);
        return new RoomRowViewModel("RUN", string.Empty, _Clock(rows.Sum(row => row.DurationSeconds)),
            rows.All(row => row.IdleSeconds is not null) ? _Clock(rows.Sum(row => row.IdleSeconds ?? 0)) : "—", _Number(damage),
            _Spawn(spawn, isCounted), spawn is not null && !isCounted, "The rooms added up.",
            Math.Min(1, share ?? 0), share is not null, share is { } total ? Percent(total) : "—",
            share is null ? "not counted" : "your damage ÷ spawn HP",
            overkill is { } over ? Signed(over) : "—",
            !table.IsLootPerRoom ? "—" : lootKnown ? IskFormat.Whole(rows.Sum(row => row.LootIsk ?? 0)) : "no price", string.Empty);
    }

    private static string _Clock(int seconds) => TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private static string _Number(long? value) => value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "—";

    private static string _Spawn(long? hp, bool isCounted) => hp is null ? "—" : isCounted ? _Number(hp) : $"≥ {_Number(hp)}";

    private static string Percent(double fraction) => $"{Math.Round(fraction * 100):0} %";

    private static string Signed(double fraction) => $"{(fraction >= 0 ? "+" : "−")}{Math.Abs(Math.Round(fraction * 100)):0} %";
}
