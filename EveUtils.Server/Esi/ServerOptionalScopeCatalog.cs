using EveUtils.Shared.Modules.Esi;

namespace EveUtils.Server.Esi;

/// <summary>
/// Optional ESI scopes this server may use for corp-level features. These are
/// voluntary: the client shows them as opt-in checkboxes before Mode B pairing, and the user chooses
/// which to grant. Declared as <see cref="EsiScopeTarget.Server"/> so the scope registry exposes them
/// via <c>GET /api/server/scopes</c>. (Demo set for the POC — real corp features land in v2.x.)
/// </summary>
public sealed class ServerOptionalScopeCatalog : IEsiScopeCatalog
{
    public IReadOnlyList<EsiScopeRequirement> Requirements { get; } =
    [
        new EsiScopeRequirement(
            "esi-corporations.read_corporation_membership.v1", EsiScopeTarget.Server,
            "Corp roster", "Lets the server read your corporation's member list for the allowed-list sync."),
        new EsiScopeRequirement(
            "esi-universe.read_structures.v1", EsiScopeTarget.Server,
            "Structures", "Lets the server resolve structure names/info for a corp structure list."),
        new EsiScopeRequirement(
            "esi-markets.structure_markets.v1", EsiScopeTarget.Server,
            "Structure markets", "Lets the server read market orders in your corp's structures."),
    ];
}
