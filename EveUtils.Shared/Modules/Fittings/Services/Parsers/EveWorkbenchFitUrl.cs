using System.Text.RegularExpressions;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Pulls the fit id out of a pasted eveworkbench.com fit link or a bare fit id.
/// Pure text work; fetching the fit is <c>IEveWorkbenchFitClient</c>'s job.
/// </summary>
public static partial class EveWorkbenchFitUrl
{
    private const string Host = "eveworkbench.com";

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex FitIdShape();

    /// <summary>
    /// True when the input points at eveworkbench.com, whether or not it carries a fit id.
    /// </summary>
    public static bool IsEveWorkbenchLink(string input)
    {
        var host = _HostOf(input);
        return host is not null
               && (host.Equals(Host, StringComparison.OrdinalIgnoreCase)
                   || host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the fit id from an eveworkbench.com link (any segment order, query and fragment allowed) or a bare id.
    /// </summary>
    public static bool TryParseFitId(string input, out Guid fitId)
    {
        var trimmed = input.Trim();
        if (Guid.TryParseExact(trimmed, "D", out fitId))
            return true;

        fitId = Guid.Empty;
        // Requiring the eveworkbench.com host keeps a foreign URL that happens to carry a GUID from being
        // fetched from EVE Workbench. The id is taken by shape, not by position: links carry the ship slug
        // before or after it, so "the last path segment" would pick the slug half the time.
        if (!IsEveWorkbenchLink(trimmed))
            return false;

        var match = FitIdShape().Match(trimmed);
        return match.Success && Guid.TryParse(match.Value, out fitId);
    }

    private static string? _HostOf(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace))
            return null;

        var absolute = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        return Uri.TryCreate(absolute, UriKind.Absolute, out var uri) ? uri.Host : null;
    }
}
