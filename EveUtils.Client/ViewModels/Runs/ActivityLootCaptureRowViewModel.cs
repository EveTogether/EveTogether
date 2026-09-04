using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One clipboard snapshot behind an activity. An excluded capture keeps its row and its lines and counts towards
/// nothing: leaving it out would balance the totals just as well, but then nobody could see afterwards that it was
/// ever taken (ET-65 AC-6).
/// </summary>
public sealed class ActivityLootCaptureRowViewModel(
    RunLootCaptureDto capture, IReadOnlyList<ActivityLootLineViewModel> lines)
{
    public bool IsExcluded { get; } = capture.IsExcluded;

    public string CapturedAtText { get; } = capture.CapturedAtUtc.ToLocalTime().ToString("HH:mm:ss");

    public string StateText { get; } = capture.IsExcluded ? "excluded — counts towards nothing" : "counted";

    public IReadOnlyList<ActivityLootLineViewModel> Lines { get; } = lines;
}
