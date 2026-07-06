using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Transport;
using EveUtils.Shared.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Server identity is the TLS cert fingerprint, not the address: the same server reached via
/// different address spellings shares one pinned fingerprint and must be treated as ONE server, so a character coupled
/// under one spelling is found when a fleet was loaded under another — the fleet-join "3rd character missing" fix.
/// </summary>
public class ServerIdentityFingerprintTests
{
    private const string AddrA = "eve-together.com:7443";
    private const string AddrB = "www.eve-together.com:7443";
    private const string Fingerprint = "AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12";

    [AvaloniaFact]
    public async Task SameFingerprintAcrossAddresses_IsOneServer_AndFindsEveryCharacter()
    {
        using var instance = TestClientInstance.Create(_ => { });
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        var trust = instance.Services.GetRequiredService<IServerTrustStore>();

        // Two spellings of the same VPS → same pinned cert fingerprint.
        trust.Pin(AddrA, Fingerprint);
        trust.Pin(AddrB, Fingerprint);

        await sessions.SaveAsync(AddrA, new ClientSessionTokens("t", "r", "Char1", 1));
        await sessions.SaveAsync(AddrA, new ClientSessionTokens("t", "r", "Char2", 2));
        await sessions.SaveAsync(AddrB, new ClientSessionTokens("t", "r", "Char3", 3)); // coupled under the other spelling

        // One logical server (one canonical address), not two.
        Assert.Single(await sessions.ListServersAsync());

        // All three characters resolve regardless of which spelling the fleet/picker queries.
        var viaA = await sessions.LoadAllAsync(AddrA);
        var viaB = await sessions.LoadAllAsync(AddrB);
        Assert.Equal(new[] { 1, 2, 3 }, viaA.Select(s => s.CharacterId).OrderBy(x => x));
        Assert.Equal(new[] { 1, 2, 3 }, viaB.Select(s => s.CharacterId).OrderBy(x => x));

        // The fix: Char3 (coupled under B) is found when acting via A — the join-picker scopes to A's fleet rows.
        var char3ViaA = await sessions.LoadForCharacterAsync(AddrA, 3);
        Assert.NotNull(char3ViaA);
        Assert.Equal("Char3", char3ViaA!.CharacterName);

        // The character's coupled-server list also dedups to one identity.
        Assert.Single(await sessions.ListServersForCharacterAsync(3));
    }

    [AvaloniaFact]
    public async Task DifferentFingerprints_StayTwoServers()
    {
        using var instance = TestClientInstance.Create(_ => { });
        var sessions = instance.Services.GetRequiredService<IClientSessionStore>();
        var trust = instance.Services.GetRequiredService<IServerTrustStore>();

        trust.Pin("alpha:7443", "AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111");
        trust.Pin("bravo:7443", "BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222");
        await sessions.SaveAsync("alpha:7443", new ClientSessionTokens("t", "r", "A", 1));
        await sessions.SaveAsync("bravo:7443", new ClientSessionTokens("t", "r", "B", 2));

        Assert.Equal(2, (await sessions.ListServersAsync()).Count);
    }
}
