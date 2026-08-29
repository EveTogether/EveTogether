using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Esi.Events;

/// <summary>
/// One character's ESI session works: its token was still valid, or it was successfully refreshed
/// against EVE SSO. Published per character so the UI and other services follow the real token state
/// instead of re-deriving it from "a token file exists" (ET-24).
/// Local-only — a token outcome is this machine's business and never goes over the external bus.
/// </summary>
public sealed class TokenRefreshedEvent(TokenStatusChange data)
    : IntegrationEvent<TokenStatusChange>(data, data.CharacterId)
{
    public override string EventType => "esi.token.refreshed";
}
