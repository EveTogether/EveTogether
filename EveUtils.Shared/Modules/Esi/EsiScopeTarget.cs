namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// Where an ESI scope is needed. The registry loads only the relevant subset per host:
/// the client loads <see cref="Client"/> and <see cref="Both"/>; the server loads <see cref="Server"/>
/// and <see cref="Both"/>. Uses flags so bitwise checks work cleanly.
/// </summary>
[Flags]
public enum EsiScopeTarget
{
    Client = 1,
    Server = 2,
    Both   = Client | Server
}
