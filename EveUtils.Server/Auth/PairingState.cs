namespace EveUtils.Server.Auth;

/// <summary>
/// Server-side state of one in-flight pairing (single-use, TTL ~5m). The client proves it is
/// the initiator at claim time via the pairing secret whose hash is <see cref="PairingChallenge"/>.
/// </summary>
public sealed class PairingState
{
    public required string PairingId { get; init; }
    public required string PairingChallenge { get; init; } // b64url(SHA256(pairing_secret))
    public required string OAuthState { get; init; }       // CSRF binding for the SSO callback
    public required DateTimeOffset CreatedAt { get; init; }

    public PairingStatus Status { get; set; } = PairingStatus.Pending;
    public string? FailureMessage { get; set; }

    // Filled when the SSO callback has been relayed + exchanged successfully.
    public int CharacterId { get; set; }
    public string? CharacterName { get; set; }
    public string? CorporationName { get; set; }
    public string? AllianceName { get; set; }
    public string? SessionToken { get; set; }
    public string? SessionRefreshToken { get; set; }
}
