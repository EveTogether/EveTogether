namespace EveUtils.Client.Updates;

/// <summary>
/// A build on offer, as the update feed carries it. <c>Url</c> is the release page to read about it.
/// </summary>
public sealed record AppRelease(string Version, string Notes, string Url);
