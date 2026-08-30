using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-63: ESI reports a character's location as a solar-system <i>id</i>, and nothing else in the app can turn one
/// into a name — the SDE import carries no universe tables. This is that one route, and what these cover is the
/// thrift around it: the caller is driven by a 6 s poll, so "ask ESI once" has to be true of a component that is
/// asked over and over.
/// </summary>
public class SolarSystemNamesTests
{
    private const int Jita = 30000142;

    /// <summary>
    /// A public call: no character, no scope, no token. That is what lets it answer for a system a pilot with no
    /// ESI sign-in at all is standing in, and it spends no scoped budget.
    /// </summary>
    [Fact]
    public async Task ItResolvesAnId_ToTheSystemName_OverThePublicEndpoint()
    {
        var esi = new FakeEsi { [Jita] = "Jita" };

        Assert.Equal("Jita", await Build(esi).NameAsync(Jita));
        Assert.Equal(1, esi.Calls);
        Assert.Equal("/universe/systems/30000142/", esi.LastRequest?.Path);
        Assert.Null(esi.LastRequest?.CharacterId);
        Assert.Empty(esi.LastRequest!.Scopes);
    }

    /// <summary>A solar system's name never changes, so asking twice is asking for nothing.</summary>
    [Fact]
    public async Task AResolvedName_IsNeverAskedForTwice()
    {
        var esi = new FakeEsi { [Jita] = "Jita" };
        var names = Build(esi);

        for (var i = 0; i < 10; i++)
            Assert.Equal("Jita", await names.NameAsync(Jita));

        Assert.Equal(1, esi.Calls);
    }

    /// <summary>
    /// Every watch starts together at start-up, so six characters parked in one system ask at the same instant. One
    /// request, not six: the callers share the lookup that is already under way.
    /// </summary>
    [Fact]
    public async Task CallersAskingAtOnce_ForTheSameSystem_ShareOneRequest()
    {
        var gate = new TaskCompletionSource();
        var esi = new FakeEsi { [Jita] = "Jita", Hold = gate.Task };
        var names = Build(esi);

        var asking = Enumerable.Range(0, 6).Select(_ => names.NameAsync(Jita)).ToArray();
        gate.SetResult();

        Assert.All(await Task.WhenAll(asking), name => Assert.Equal("Jita", name));
        Assert.Equal(1, esi.Calls);
    }

    /// <summary>
    /// A failure leaves the location unknown, exactly as it was before any of this existed. Nothing blocks and
    /// nothing throws at the caller — filling a gap is not worth an exception it would have to guard.
    /// </summary>
    [Fact]
    public async Task AFailedLookup_IsNull_NotAnException()
    {
        var esi = new FakeEsi { Error = EsiErrorKind.ServerError };

        Assert.Null(await Build(esi).NameAsync(Jita));
    }

    /// <summary>
    /// The one that matters for ESI's error budget. The caller retries while the gap is open, and the gap stays
    /// open exactly as long as the lookup keeps failing — so without a pause, one unfilled location would be ten
    /// requests a minute for the whole of an outage.
    /// </summary>
    [Fact]
    public async Task AFailedLookup_IsHeldBack_InsteadOfRetriedOnEveryAsk()
    {
        var esi = new FakeEsi { Error = EsiErrorKind.ServerError };
        var names = Build(esi);

        for (var i = 0; i < 50; i++)
            Assert.Null(await names.NameAsync(Jita));

        Assert.Equal(1, esi.Calls);
    }

    /// <summary>…but held back, not given up on: once the pause is out the gap can still close on its own.</summary>
    [Fact]
    public async Task AFailedLookup_IsTriedAgain_OnceThePauseIsOut()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var esi = new FakeEsi { Error = EsiErrorKind.ServerError };
        var names = new SolarSystemNames(esi, NullLogger<SolarSystemNames>.Instance)
        {
            RetryAfter = TimeSpan.FromMinutes(5),
            Clock = () => now,
        };

        Assert.Null(await names.NameAsync(Jita));
        Assert.Equal(1, esi.Calls);

        now = now.AddMinutes(6);
        esi.Error = null;
        esi[Jita] = "Jita";

        Assert.Equal("Jita", await names.NameAsync(Jita));
        Assert.Equal(2, esi.Calls);
    }

    /// <summary>An id ESI has never heard of is a null, not a fabricated name.</summary>
    [Fact]
    public async Task AnUnknownId_ResolvesToNothing()
    {
        var esi = new FakeEsi();   // answers, but with no name

        Assert.Null(await Build(esi).NameAsync(Jita));
    }

    /// <summary>A non-positive id never reaches ESI: there is no such system and the 404 would only cost budget.</summary>
    [Fact]
    public async Task AnImpossibleId_IsNotSentToEsi()
    {
        var esi = new FakeEsi();

        Assert.Null(await Build(esi).NameAsync(0));
        Assert.Null(await Build(esi).NameAsync(-1));
        Assert.Equal(0, esi.Calls);
    }

    private static SolarSystemNames Build(FakeEsi esi) => new(esi, NullLogger<SolarSystemNames>.Instance);

    private sealed class FakeEsi : IEsiClient
    {
        private readonly ConcurrentDictionary<int, string> _names = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public EsiErrorKind? Error { get; set; }
        public EsiRequest? LastRequest { get; private set; }

        /// <summary>Blocks every answer until it completes, so a test can have several callers in flight at once.</summary>
        public Task? Hold { get; init; }

        public string this[int solarSystemId] { set => _names[solarSystemId] = value; }

        public async Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;

            if (Hold is { } hold)
                await hold;

            if (Error is { } error)
                return EsiResult<T>.Fail(EsiError.Of(error, "fake"));

            var id = int.Parse(request.Path.Trim('/').Split('/')[^1], CultureInfo.InvariantCulture);
            var system = new EsiSolarSystem { Name = _names.GetValueOrDefault(id) };
            return EsiResult<T>.Ok((T)(object)system);
        }
    }
}
