namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// CCP static-data endpoints. <c>latest.jsonl</c> is a one-line manifest carrying the current build number;
/// the data zip URL is build-numbered (the unversioned "latest" alias is access-denied on the CDN — verified
/// 2026-06-05, build 3374020). Variant = full tranquility JSONL.
/// </summary>
public static class SdeEndpoints
{
    public const string HttpClientName = "sde";

    private const string Base = "https://developers.eveonline.com/static-data/tranquility";

    public const string LatestManifest = Base + "/latest.jsonl";

    public static string DataZip(long buildNumber) =>
        $"{Base}/eve-online-static-data-{buildNumber}-jsonl.zip";
}
