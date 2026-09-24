namespace EveUtils.Shared.Modules.Sde.Dtos;

public sealed record SdeSkill(int TypeId, string Name, int Rank, int PrimaryAttributeId, int SecondaryAttributeId, bool Published);
