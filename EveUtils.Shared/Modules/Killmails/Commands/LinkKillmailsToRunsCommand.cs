using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Killmails.Commands;

/// <summary>Links each of the character's unlinked losses to its run by <see cref="KillmailRunLinker"/> (ET-331) and
/// adds the affected activities up again. A pilot's own choice, linked or unlinked, is never touched. Returns how many
/// losses were linked.</summary>
public sealed record LinkKillmailsToRunsCommand(int CharacterId) : ICommand<Result<int>>;
