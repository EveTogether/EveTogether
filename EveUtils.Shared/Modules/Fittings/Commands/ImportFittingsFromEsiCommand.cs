using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Dtos;

namespace EveUtils.Shared.Modules.Fittings.Commands;

/// <summary>
/// Persists selected ESI fittings to the local DB.
/// The client first fetches all fits via <see cref="EveUtils.Shared.Modules.Fittings.Services.IFittingEsiClient"/>
/// and lets the user choose which to import. Only the fitting IDs listed in
/// <see cref="SelectedFittingIds"/> are stored; pass null or empty to import all.
/// The full <see cref="EsiFittings"/> list must be supplied so the handler can look up the raw JSON
/// without a second ESI round-trip.
/// </summary>
public sealed record ImportFittingsFromEsiCommand(
    int CharacterId,
    IReadOnlyList<EsiFitting> EsiFittings,
    IReadOnlyList<int>? SelectedFittingIds = null) : ICommand<Result<int>>;
