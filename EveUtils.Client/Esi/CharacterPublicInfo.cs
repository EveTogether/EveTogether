namespace EveUtils.Client.Esi;

/// <summary>Public affiliation of a character (no token needed). Names/tickers are null when unavailable.</summary>
public sealed record CharacterPublicInfo(string? CorporationName, string? CorporationTicker, string? AllianceName, string? AllianceTicker)
{
    /// <summary>"Corp [TICK] · Alliance [TICK]" / "Corp [TICK]" / "—".</summary>
    public string AffiliationLabel
    {
        get
        {
            string? corp = CorporationName is null ? null : CorporationTicker is null ? CorporationName : $"{CorporationName} [{CorporationTicker}]";
            string? ally = AllianceName is null ? null : AllianceTicker is null ? AllianceName : $"{AllianceName} [{AllianceTicker}]";
            return corp is null && ally is null ? "—" : ally is null ? corp! : $"{corp} · {ally}";
        }
    }
}
