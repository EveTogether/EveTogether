using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>Tries every kept unrecognised loot name against the SDE again (ET-460). A name the SDE now knows becomes a
/// loot entry of the capture it came from, so it counts exactly as the paste would have; the result is how many lines
/// moved. Saved runs that gain a line are marked corrected, so a published one reads "changed since published".</summary>
public sealed record RepriceUnrecognisedLootCommand : ICommand<Result<int>>;
