namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Normalises a concrete ESI request path into a route template so per-endpoint metrics aggregate across ids
/// instead of exploding into one row per character/corp/alliance. ESI ids are numeric path segments, so every
/// all-digits segment collapses to <c>{id}</c> (e.g. <c>/characters/2114794365/</c> → <c>/characters/{id}/</c>).
/// A query string, if any, is dropped — metrics group by route, not by parameters.
/// </summary>
public static class EsiEndpointKey
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var queryIndex = path.IndexOf('?');
        var route = queryIndex >= 0 ? path[..queryIndex] : path;

        var segments = route.Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (IsAllDigits(segments[i]))
                segments[i] = "{id}";

        return string.Join('/', segments);
    }

    private static bool IsAllDigits(string segment)
    {
        if (segment.Length == 0)
            return false;
        foreach (var c in segment)
            if (c is < '0' or > '9')
                return false;
        return true;
    }
}
