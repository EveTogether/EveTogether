namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// File locations for the SDE store. <see cref="DatabasePath"/> is the live read-only store the accessor opens;
/// <see cref="WorkDirectory"/> holds the downloaded zip and the temp build before the atomic swap. Both are
/// anchored to each host's data directory.
/// </summary>
public sealed class SdeOptions
{
    public required string DatabasePath { get; init; }
    public required string WorkDirectory { get; init; }
}
