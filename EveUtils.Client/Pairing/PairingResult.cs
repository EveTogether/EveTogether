namespace EveUtils.Client.Pairing;

public sealed record PairingResult(
    string CharacterName,
    int CharacterId,
    string ServerName,
    string CorporationName,
    string AllianceName);
