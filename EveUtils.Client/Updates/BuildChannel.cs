namespace EveUtils.Client.Updates;

// Which stream a build belongs to, read from the build's own version (ET-339, mirrors Cockpit's AC-387).
// Defaulting to Stable instead would offer a nightly copy with no configuration a "stable" update that is really
// a downgrade — the build already knows what it is, so ask it rather than assume.
public static class BuildChannel
{
    private const string Nightly = "nightly";

    // Only the nightly prerelease tag means nightly; every other prerelease (e.g. "-alpha", "-beta") reads as
    // stable, the answer that offers less.
    public static UpdateChannel FromVersion(string version)
    {
        var text = version.Trim();

        var build = text.IndexOf('+');
        if (build >= 0)
            text = text[..build];

        var dash = text.IndexOf('-');

        return dash >= 0 && text[(dash + 1)..].StartsWith(Nightly, StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Nightly
            : UpdateChannel.Stable;
    }
}
