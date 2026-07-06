namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Internal marker header the <see cref="EsiGatingHandler"/> adds to its synthetic 503 so the pivot can tell a call
/// it withheld locally (ESI is down) from a real server 503. Never sent to ESI; only travels back up the chain.
/// </summary>
public static class EsiGateHeaders
{
    /// <summary>Present on the synthetic response the gate returns for a call it withheld during downtime.</summary>
    public const string Withheld = "X-EveUtils-Gate-Withheld";
}
