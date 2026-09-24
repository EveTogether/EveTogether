using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Killmails.Dtos;

namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One killmail row on the KILLMAILS overview (ET-332): built once per read from
/// <see cref="KillmailOverviewRowDto"/> plus the ship, system and counterparty names <see cref="KillmailsOverviewViewModel"/>
/// already resolved for the whole read, the same split <c>ActivityOverviewRowViewModel</c> draws from its facts and
/// name delegates. <see cref="OpenAsync"/> is a no-op until ET-333 hands in a real one, the same seam
/// <c>ActivityOverviewRowViewModel._openDetail</c> uses for its own detail screen.</summary>
public sealed partial class KillmailRowViewModel : ObservableObject
{
    private readonly Func<KillmailRowViewModel, Task> _openDetail;

    public KillmailRowViewModel(KillmailOverviewRowDto dto, string shipName, string systemName, string? regionName,
        bool isAbyssal, string securityText, string counterpartyName, Func<KillmailRowViewModel, Task> openDetail)
    {
        _openDetail = openDetail;
        CharacterId = dto.CharacterId;
        KillmailId = dto.KillmailId;
        IsLoss = dto.IsLoss;
        KillmailTimeUtc = dto.KillmailTimeUtc;
        AttackerCount = dto.AttackerCount;
        RunId = dto.RunId;
        NotLinkedCandidateCount = dto.NotLinkedCandidateCount;
        ShipName = shipName;
        SystemName = systemName;
        IsAbyssal = isAbyssal;
        CounterpartyName = counterpartyName;
        Isk = dto.IskValue is { } value ? (dto.IsLoss ? -value : value) : null;

        DateTime local = dto.KillmailTimeUtc.ToLocalTime();
        Day = DateOnly.FromDateTime(local);
        TimeText = local.ToString("HH:mm");
        KindGlyph = dto.IsLoss ? "▼" : "▲";
        ShipText = dto.IsLoss ? $"{shipName} — lost" : shipName;
        SystemLineText = isAbyssal
            ? $"Abyssal deadspace · {dto.SolarSystemId}"
            : regionName is null ? systemName : $"{systemName} · {regionName}";
        SecurityText = securityText;
        AttackerCountText = !dto.IsLoss && dto.AttackerCount == 1 ? "solo"
            : dto.AttackerCount == 1 ? "1 attacker" : $"{dto.AttackerCount} attackers";
        IskText = Isk is { } signed ? IskFormat.Compact(signed) : "no price";
    }

    public int CharacterId { get; }

    public int KillmailId { get; }

    public bool IsLoss { get; }

    public DateTime KillmailTimeUtc { get; }

    /// <summary>The local calendar day this row groups under (ET-332 AC5) — never UTC, so a mail just after local
    /// midnight does not fall under the previous UTC day's header.</summary>
    public DateOnly Day { get; }

    public string TimeText { get; }

    public string KindGlyph { get; }

    public string ShipText { get; }

    public string SystemLineText { get; }

    public string SecurityText { get; }

    public bool IsAbyssal { get; }

    public int AttackerCount { get; }

    public string AttackerCountText { get; }

    public Guid? RunId { get; }

    /// <summary>How many of this character's own runs the loss could belong to, when it is not linked — 0 means
    /// nothing fits, more than one means the pilot has to choose (ET-332).</summary>
    public int NotLinkedCandidateCount { get; }

    public bool ShowNotLinkedChip => IsLoss && RunId is null && NotLinkedCandidateCount > 1;

    public bool ShowLinkedChip => IsLoss && RunId is not null;

    public string NotLinkedChipText => $"NOT LINKED · {NotLinkedCandidateCount} runs match";

    public string CounterpartyName { get; }

    /// <summary>ISK signed for the day and totals: negative for a loss, positive for a kill, null when nothing
    /// about the ship or its items is priced (ET-332 AC6) — never a plain 0, which would read as a free kill.</summary>
    public decimal? Isk { get; }

    public string IskText { get; }

    private string ShipName { get; }

    private string SystemName { get; }

    /// <summary>Ship, system or counterparty name contains <paramref name="needle"/> (ET-332 AC4) — case-insensitive,
    /// the same three fields the SHOW row's search box placeholder promises.</summary>
    public bool Matches(string needle) =>
        ShipName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        SystemName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
        CounterpartyName.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private Task Open() => _openDetail(this);
}
