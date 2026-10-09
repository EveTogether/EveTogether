using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Runs.Dtos;

public sealed record RunHitTallyDto(
    DamageDirection Direction, string Counterparty, string? Weapon, HitQuality Quality, int Count, long Sum, int Min, int Max);
