using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Shared.Modules.Killmails;

/// <summary>
/// ESI scope declarations for the Killmails module. <c>GET /characters/{id}/killmails/recent/</c> lists a character's
/// kills and losses of the last 90 days; granted by default because killmails are history, not live data.
/// </summary>
public static class KillmailsScopeCatalog
{
    public const string ReadKillmails = "esi-killmails.read_killmails.v1";

    public static IEsiScopeCatalog Catalog { get; } = new KillmailsEsiScopeCatalogImpl();

    private sealed class KillmailsEsiScopeCatalogImpl : IEsiScopeCatalog
    {
        public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
        [
            new EsiScopeRequirement(ReadKillmails, EsiScopeTarget.Client, "Killmails",
                "Required to read your character's kills and losses, to show them and link a lost ship to its run."),
        ];
    }
}
