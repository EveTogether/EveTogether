using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One clipboard snapshot on the running run. <see cref="RepeatOfCapturedAtUtc"/> is set when this capture's
/// hash matches an earlier one on the same run — the "identical to HH:mm:ss" the momentopname-besluit calls for —
/// and is worked out client-side from the whole list rather than stored, so it never needs a migration of its own.</summary>
public sealed partial class RunLootCaptureRowViewModel : ObservableObject
{
    public Guid CaptureId { get; }
    public DateTime CapturedAtUtc { get; }
    public DateTime? RepeatOfCapturedAtUtc { get; }
    public IReadOnlyList<RunLootEntryDto> Entries { get; }

    public string CapturedAtDisplay => CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    public string? RepeatOfDisplay => RepeatOfCapturedAtUtc is { } capturedAt
        ? $"identical to {capturedAt.ToLocalTime():HH:mm:ss}"
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InOutText))]
    private bool _isExcluded;

    public string InOutText => IsExcluded ? "OUT" : "IN";

    public RunLootCaptureRowViewModel(RunLootCaptureDto dto, DateTime? repeatOfCapturedAtUtc)
    {
        CaptureId = dto.CaptureId;
        CapturedAtUtc = dto.CapturedAtUtc;
        RepeatOfCapturedAtUtc = repeatOfCapturedAtUtc;
        Entries = dto.Entries;
        _isExcluded = dto.IsExcluded;
    }
}
