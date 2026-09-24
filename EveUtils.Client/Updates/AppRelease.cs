using System;

namespace EveUtils.Client.Updates;

/// <summary>
/// A build on offer, with a label from the update feed. <c>Url</c> is the release page to read about it, and
/// <c>SizeBytes</c> the download the user is about to agree to.
/// </summary>
public sealed record AppRelease(string Version, string Notes, string Url, long SizeBytes = 0)
{
    public string DisplayVersion => Version.Contains("-nightly.", StringComparison.Ordinal) ? Version : $"v{Version}";
}
