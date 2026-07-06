using EveUtils.Client.Transport;
using EveUtils.Shared.Transport;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The pinned channel is cached and reused per server (long-lived, thread-safe), but can be invalidated so the next
/// build is fresh. The bus reconnect loop uses that to recover from a channel that wedged on a dead connection
/// after a server restart instead of reusing it forever (which previously only a client restart cleared). The
/// reconnect-recovery itself is runtime gRPC behaviour, verified by hand; this covers the cache + invalidation mechanic.
/// </summary>
public class GrpcChannelFactoryTests
{
    private const string Address = "https://localhost:5001";

    [Fact]
    public void CreatePinned_ReturnsTheSameCachedChannel_UntilInvalidated()
    {
        using var factory = new GrpcChannelFactory(new FakeTrustStore("AABBCC"));

        var first = factory.CreatePinned(Address);
        Assert.Same(first, factory.CreatePinned(Address)); // cached + reused for the session

        factory.Invalidate(Address);

        Assert.NotSame(first, factory.CreatePinned(Address)); // a fresh channel after invalidation
    }

    [Fact]
    public void Invalidate_OnAnUnknownServer_IsANoOp()
    {
        using var factory = new GrpcChannelFactory(new FakeTrustStore("AABBCC"));
        factory.Invalidate("https://not-cached:1"); // nothing cached — must not throw
    }

    private sealed class FakeTrustStore(string fingerprint) : IServerTrustStore
    {
        public string? GetFingerprint(string serverAddress) => fingerprint;
        public void Pin(string serverAddress, string fingerprint) { }
    }
}
