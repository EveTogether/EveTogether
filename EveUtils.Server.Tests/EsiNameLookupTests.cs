using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Server.Esi;
using EveUtils.Shared.Modules.Esi.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The panel's ESI name lookup must not go back to ESI on every pageview (and every 2s tick), whether the last
/// answer was a name or a failure — neither is visible in a render, which is why this is pinned.
/// </summary>
public sealed class EsiNameLookupTests
{
    [Fact]
    public async Task A_resolved_name_is_served_from_memory_on_the_next_lookup()
    {
        var esi = new CountingEsi(EsiResult<List<EsiNameLookup.NameEntry>>.Ok(
            [new EsiNameLookup.NameEntry { Category = "character", Id = 95465499, Name = "FC Bartender" }]));
        var lookup = new EsiNameLookup(esi, NullLogger<EsiNameLookup>.Instance, TimeProvider.System);

        await lookup.ResolveAsync([95465499], TestContext.Current.CancellationToken);
        var second = await lookup.ResolveAsync([95465499], TestContext.Current.CancellationToken);

        Assert.Equal(1, esi.Calls);
        Assert.Equal("FC Bartender", second[95465499]);
    }

    [Fact]
    public async Task A_failed_lookup_yields_no_name_and_is_not_retried_straight_away()
    {
        var esi = new CountingEsi(EsiResult<List<EsiNameLookup.NameEntry>>.Fail(
            EsiError.Of(EsiErrorKind.ServerError, "ESI is down", 503)));
        var lookup = new EsiNameLookup(esi, NullLogger<EsiNameLookup>.Instance, TimeProvider.System);

        await lookup.ResolveAsync([7], TestContext.Current.CancellationToken);
        var second = await lookup.ResolveAsync([7], TestContext.Current.CancellationToken);

        Assert.Equal(1, esi.Calls);
        Assert.Empty(second);
    }

    private sealed class CountingEsi(EsiResult<List<EsiNameLookup.NameEntry>> answer) : IEsiClient
    {
        public int Calls { get; private set; }

        public Task<EsiResult<T>> RequestAsync<T>(EsiRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult((EsiResult<T>)(object)answer);
        }
    }
}
