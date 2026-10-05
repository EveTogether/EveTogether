using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>Removes a plan's row for this (skill, level) — the dedupe key identifies a row unambiguously, the same
/// natural-key match <c>RemoveMatchingProvisionalKillmailCommand</c> uses instead of a synthetic row id.</summary>
public sealed record RemoveSkillPlanRowCommand(int CharacterId, int PlanId, int SkillTypeId, int Level) : ICommand<Result>;
