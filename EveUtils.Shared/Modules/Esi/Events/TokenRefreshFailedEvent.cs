using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Esi.Events;

/// <summary>
/// One character's ESI session does not work: <see cref="TokenStatus.NeedsReauth"/> (the refresh token was
/// rejected — only a fresh sign-in fixes it), <see cref="TokenStatus.TemporarilyUnavailable"/> (the refresh
/// returned a token that fails validation, usually clock skew — transient, re-auth would not help), or
/// <see cref="TokenStatus.NoToken"/> (nothing stored for this character at all). They are deliberately one
/// event with the status attached: a subscriber that only wants to know "this character cannot call ESI
/// right now" needs one subscription, and the ones that care why read <c>Data.Status</c>.
/// Local-only — a token outcome is this machine's business and never goes over the external bus.
/// </summary>
public sealed class TokenRefreshFailedEvent(TokenStatusChange data)
    : IntegrationEvent<TokenStatusChange>(data, data.CharacterId)
{
    public override string EventType => "esi.token.refresh-failed";
}
