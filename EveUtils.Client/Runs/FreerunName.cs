using System;
using System.Globalization;

namespace EveUtils.Client.Runs;

/// <summary>What a freerun is called (ET-508): "Freerun" and its local start moment. Never a system or a site: the
/// name travels to the fleet and the server, and a roam's whereabouts stay out of it (OPSEC).</summary>
public static class FreerunName
{
    public static string For(DateTime startedAtUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"Freerun {startedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
}
