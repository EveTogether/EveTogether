using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>One kind of item that counts on the sharing pilot's run, its copies added up; <see cref="LootKind.Lost"/>
/// for what was spent rather than picked up. No name: the receiver has the same SDE to read it from.</summary>
public sealed record RunShareLootLine(int TypeId, long Quantity, LootKind Kind);
