using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>A run that was thrown away but is still on disk, so it can be brought back (ET-511).</summary>
public sealed class RecentlyDiscardedRunDto
{
    public required Guid RunId { get; init; }
    public required long CharacterId { get; init; }
    public required ActivityKind ActivityKind { get; init; }
    public required string? SiteName { get; init; }
    public required string? SignatureGroupSnapshot { get; init; }
    public required int SiteTypeId { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required DateTime DiscardedAtUtc { get; init; }
}
