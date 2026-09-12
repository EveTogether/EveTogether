using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>What the app proposes for one character before anybody touches the tick (ET-230).</summary>
/// <param name="IsAddedToLastSite">Evidence ticked a character the previous site of the series ended without — the one
/// way a carried-over list may change by itself.</param>
public sealed record AttendanceProposalLine(
    long CharacterId, bool IsInSite, AttendanceReason Reason, long? Amount, bool IsAddedToLastSite = false);
