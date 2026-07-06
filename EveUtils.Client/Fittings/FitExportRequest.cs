using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Dialogs;

namespace EveUtils.Client.Fittings;

/// <summary>
/// Per-call boundary for <see cref="IFitExportActions"/>. The seam itself is stateless; the
/// view-model state a fit export action needs — which fit, how to build the character picker, where status text
/// goes, and an optional hook after a successful share — travels in here so the Local tab, the fit-detail window
/// and the fit-browser rows can all drive the same actions.
/// </summary>
/// <param name="FitId">The local fitting id (<see cref="EveUtils.Shared.Modules.Fittings.Entities.LocalFitting.Id"/>).</param>
/// <param name="FitName">Display name used in status/prompt text.</param>
/// <param name="PickOptionsFor">Builds character-picker options for a required ESI scope (the former
/// <c>BuildPickOptions</c>); the caller owns the character list and its per-character scope flags.</param>
/// <param name="ReportStatus">Sink for human-readable status updates (the former <c>FittingsStatus</c> setter).</param>
/// <param name="OnSharedToServer">Optional callback invoked with the target server address after a fit is
/// accepted by that server — lets a caller refresh the matching server tab. Null when the caller has no
/// such tab.</param>
public sealed record FitExportRequest(
    int FitId,
    string FitName,
    Func<string, IReadOnlyList<CharacterPickOption>> PickOptionsFor,
    Action<string> ReportStatus,
    Func<string, Task>? OnSharedToServer = null);
