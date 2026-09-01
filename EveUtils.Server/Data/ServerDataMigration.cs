namespace EveUtils.Server.Data;

/// <summary>
/// What a one-off move out of the pre-ET-94 data directory did: the entries that were moved, and the ones that
/// were left behind because they could not be moved (only ever the self-rebuilding caches — see
/// <see cref="ServerDataDirectory.MigrateLegacyContents"/>).
/// </summary>
internal sealed record ServerDataMigration(IReadOnlyList<string> Moved, IReadOnlyList<string> LeftBehind)
{
    public static ServerDataMigration None { get; } = new([], []);

    public bool Ran => Moved.Count > 0 || LeftBehind.Count > 0;
}
