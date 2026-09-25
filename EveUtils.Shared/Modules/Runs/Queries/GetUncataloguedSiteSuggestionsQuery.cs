using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Shared.Modules.Runs.Queries;

/// <summary>Every site name an earlier run recorded without a catalogue match, with the scanner group it was
/// recorded under — the manual start dialog's suggestions for sites CCP never publishes in the SDE (exploration
/// relic and data sites). One row per name, carrying the group of its most recent run; runs that recorded no group
/// are left out, since a suggestion without one would still need asking.</summary>
public sealed record GetUncataloguedSiteSuggestionsQuery : IQuery<Result<IReadOnlyList<UncataloguedSiteSuggestionDto>>>;
