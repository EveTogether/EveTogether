using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Esi;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The app-wide ESI-scope gate: a feature is allowed when the acting character has granted every required
/// scope; otherwise the reason names the missing scope + its human feature + how to grant it, for a disabled tooltip
/// or a toast.
/// </summary>
public class EsiScopeGateTests
{
    private const int Pilot = 100;
    private const string ReadFleet = "esi-fleets.read_fleet.v1";
    private const string WriteFleet = "esi-fleets.write_fleet.v1";

    private static EsiScopeGate Gate(Character character) =>
        new(new FakeCharacterRegistry(character),
            new FakeScopeRegistry(
                new EsiScopeRequirement(ReadFleet, EsiScopeTarget.Client, "Live fleet"),
                new EsiScopeRequirement(WriteFleet, EsiScopeTarget.Client, "Fleet control")));

    [Fact]
    public async Task NoScopesRequired_IsAllowed()
    {
        var state = await Gate(new Character("Jithran", Pilot, [])).EvaluateAsync(Pilot, [], TestContext.Current.CancellationToken);
        Assert.True(state.IsAllowed);
    }

    [Fact]
    public async Task CharacterHasAllScopes_IsAllowed()
    {
        var state = await Gate(new Character("Jithran", Pilot, [ReadFleet, WriteFleet]))
            .EvaluateAsync(Pilot, [ReadFleet, WriteFleet], TestContext.Current.CancellationToken);
        Assert.True(state.IsAllowed);
        Assert.Empty(state.MissingScopes);
    }

    [Fact]
    public async Task MissingScope_IsNotAllowed_AndReasonNamesScopeFeatureAndCharacter()
    {
        var state = await Gate(new Character("Jithran", Pilot, [ReadFleet]))
            .EvaluateAsync(Pilot, [ReadFleet, WriteFleet], TestContext.Current.CancellationToken);

        Assert.False(state.IsAllowed);
        Assert.Equal([WriteFleet], state.MissingScopes); // only the un-granted one
        Assert.Contains("Fleet control", state.Reason!); // the human feature
        Assert.Contains(WriteFleet, state.Reason!);       // the raw scope, for precision
        Assert.Contains("Jithran", state.Reason!);        // who to re-sign-in
    }

    [Fact]
    public async Task UnknownCharacter_IsNotAllowed()
    {
        var state = await Gate(new Character("Someone", 999, [ReadFleet, WriteFleet]))
            .EvaluateAsync(Pilot, [ReadFleet], TestContext.Current.CancellationToken); // Pilot is not in the registry
        Assert.False(state.IsAllowed);
        Assert.Equal([ReadFleet], state.MissingScopes);
    }

    private sealed class FakeCharacterRegistry(params Character[] characters) : ICharacterRegistry
    {
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Character>>(characters);

        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event System.Action RegistryChanged { add { } remove { } }
    }

    private sealed class FakeScopeRegistry(params EsiScopeRequirement[] requirements) : IEsiScopeRegistry
    {
        public IReadOnlyList<string> GetScopes(EsiScopeTarget host) => requirements.Select(r => r.Scope).ToList();
        public IReadOnlyList<EsiScopeRequirement> GetRequirements(EsiScopeTarget host) => requirements;
        public bool IsRequired(string scope, EsiScopeTarget host) => requirements.Any(r => r.Scope == scope);
    }
}
