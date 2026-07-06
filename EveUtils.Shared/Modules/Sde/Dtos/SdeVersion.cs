namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// The build identity of an SDE store. <see cref="BuildNumber"/> is CCP's monotonically increasing build id
/// from <c>latest.jsonl</c>; it is the value we pin on and compare to decide whether a rebuild is needed.
/// </summary>
public sealed record SdeVersion(long BuildNumber, DateTimeOffset ReleaseDate);
