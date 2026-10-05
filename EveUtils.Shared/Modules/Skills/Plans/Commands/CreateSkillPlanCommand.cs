using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>Creates an empty named plan for a character (ET-355).</summary>
public sealed record CreateSkillPlanCommand(int CharacterId, string Name) : ICommand<Result<int>>;
