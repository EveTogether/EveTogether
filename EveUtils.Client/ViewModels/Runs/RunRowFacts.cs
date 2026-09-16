using System;
using System.Collections.Concurrent;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// What a runs-screen row asks the static data, answered once per distinct question for as long as the screen is open
/// (ET-290). A row's TYPE could fall back to a site-name lookup (ET-275) and its system is a lookup by id; each of
/// those is a <c>SqliteSdeAccessor</c> call with its own pooled connection and PRAGMA, and a month of rows asked them
/// row by row, on the UI thread. Safe to use from the thread that builds the rows.
/// </summary>
public sealed class RunRowFacts(ISdeAccessor? sde)
{
    private readonly ConcurrentDictionary<(ActivityKind Kind, string? SignatureGroup, int SiteTypeId, string? SiteName), RunTypeDefinition> _types = new();
    private readonly ConcurrentDictionary<int, SdeSolarSystem?> _systems = new();

    public RunTypeDefinition TypeOf(ActivityKind kind, string? signatureGroup, int siteTypeId, string? siteName)
    {
        var key = (kind, signatureGroup, siteTypeId, siteName);
        if (_types.TryGetValue(key, out RunTypeDefinition? known))
            return known;

        RunTypeDefinition resolved = RunTypeCatalogue.For(kind, signatureGroup, siteTypeId, sde, siteName);
        // Not kept while an SDE exists but is not imported yet: the name fallback could not run, and the answer it
        // gives once the import lands is a different one.
        if (sde is not { IsAvailable: false })
            _types[key] = resolved;
        return resolved;
    }

    /// <summary>Null when the activity recorded no system, or the static data does not know it (yet).</summary>
    public SdeSolarSystem? SystemOf(int? solarSystemId)
    {
        if (solarSystemId is not { } id || sde is not { IsAvailable: true } available)
            return null;

        return _systems.GetOrAdd(id, available.GetSolarSystem);
    }

    /// <summary>Security the way the game shows it: one decimal, and anything above 0.0 that would round down to it
    /// reads 0.1 — a system that is technically highsec-adjacent never passes for nullsec.</summary>
    public static string SecurityText(double securityStatus) =>
        securityStatus is > 0 and < 0.05
            ? "0.1"
            : Math.Round(securityStatus, 1, MidpointRounding.AwayFromZero).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
