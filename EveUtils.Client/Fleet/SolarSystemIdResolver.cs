using System;
using System.Collections.Concurrent;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Solar system name → SDE id, for the places that only get a name: a gamelog jump line and the location sample of a
/// fleet mate on an older client. Both arrive every second or on every jump, so a resolved name is remembered instead
/// of asking the SDE store again. A name the SDE does not know yet (no SDE downloaded) is not remembered, so it
/// resolves once the SDE is there.
/// </summary>
public sealed class SolarSystemIdResolver(ISdeAccessor? sde = null) : ISingletonService
{
    private readonly ConcurrentDictionary<string, int> _idByName = new(StringComparer.OrdinalIgnoreCase);

    public int? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || sde is null)
            return null;

        var key = name.Trim();
        if (_idByName.TryGetValue(key, out var known))
            return known;

        if (sde.FindSolarSystemByName(key) is not { } system)
            return null;

        _idByName[key] = system.SolarSystemId;
        return system.SolarSystemId;
    }
}
