using EveUtils.Shared.Modules.Esi.Http;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Route-template normalisation for the per-endpoint metrics dimension: numeric id segments collapse to
/// <c>{id}</c> so calls aggregate per endpoint instead of per character/corp/alliance.
/// </summary>
public class EsiEndpointKeyTests
{
    [Theory]
    [InlineData("/characters/2114794365/", "/characters/{id}/")]
    [InlineData("/corporations/98000001/", "/corporations/{id}/")]
    [InlineData("/characters/2114794365/skills/", "/characters/{id}/skills/")]
    [InlineData("/markets/10000002/orders/", "/markets/{id}/orders/")]
    [InlineData("/status/", "/status/")]
    [InlineData("/markets/prices/", "/markets/prices/")]
    [InlineData("/characters/2114794365/wallet/?page=2", "/characters/{id}/wallet/")]
    public void Normalize_CollapsesIdSegments_AndDropsQuery(string path, string expected) =>
        Assert.Equal(expected, EsiEndpointKey.Normalize(path));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Normalize_EmptyOrNull_YieldsRoot(string? path) =>
        Assert.Equal("/", EsiEndpointKey.Normalize(path));
}
