namespace EveUtils.Client.LocalApi.Dtos;

/// <param name="Tier">0 (Tranquil) to 6 (Cataclysmic); null with the weather for runs saved before the tier was kept.</param>
public sealed record AbyssalBreakdownDto(
    int? Tier,
    string? TierName,
    string? Weather,
    int Runs,
    decimal? Isk,
    decimal? IskPerHour,
    long? AverageClearSeconds);
