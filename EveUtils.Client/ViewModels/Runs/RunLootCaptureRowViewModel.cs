using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One capture on the running run. <see cref="RepeatOfCapturedAtUtc"/> is set when this capture's hash
/// matches an earlier one on the same run — the "identical to HH:mm:ss" the momentopname-besluit calls for — and is
/// worked out client-side from the whole list rather than stored, so it never needs a migration of its own.</summary>
public sealed partial class RunLootCaptureRowViewModel : ObservableObject
{
    public Guid CaptureId { get; }
    public DateTime CapturedAtUtc { get; }
    public LootCaptureSource Source { get; }
    public DateTime? RepeatOfCapturedAtUtc { get; }
    public IReadOnlyList<RunLootEntryDto> Entries { get; }

    /// <summary>Its place in the run, from 1 — what the section calls it everywhere else ("difference #2 → #4"), so
    /// the strip and the caption name the same capture by the same number.</summary>
    public int Number { get; }

    public string CapturedAtDisplay => CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    public string NumberDisplay => $"#{Number}";

    /// <summary>Where its bytes came from, which is a different question from what it is to the run.</summary>
    public string SourceText => Source is LootCaptureSource.Pasted ? "PASTED" : "CLIPBOARD";

    public string? RepeatOfDisplay => RepeatOfCapturedAtUtc is { } capturedAt
        ? $"identical to {capturedAt.ToLocalTime():HH:mm:ss}"
        : null;

    /// <summary>What this capture is to the run. Shown whether or not the paste boxes are: a pilot back on the
    /// clipboard way still has to be able to see which capture his figures are counted from.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleText))]
    [NotifyPropertyChangedFor(nameof(IsCargoBefore))]
    private LootCaptureRole _role;

    public string RoleText => Role switch
    {
        LootCaptureRole.CargoBefore => "STARTING HOLD",
        LootCaptureRole.CargoAfter => "ENDING HOLD",
        _ => "DURING THE RUN"
    };

    public bool IsCargoBefore => Role is LootCaptureRole.CargoBefore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InOutText))]
    private bool _isExcluded;

    public string InOutText => IsExcluded ? "OUT" : "IN";

    public RunLootCaptureRowViewModel(RunLootCaptureDto dto, DateTime? repeatOfCapturedAtUtc, int number)
    {
        CaptureId = dto.CaptureId;
        CapturedAtUtc = dto.CapturedAtUtc;
        Source = dto.Source;
        Number = number;
        RepeatOfCapturedAtUtc = repeatOfCapturedAtUtc;
        Entries = dto.Entries;
        _role = dto.Role;
        _isExcluded = dto.IsExcluded;
    }
}
