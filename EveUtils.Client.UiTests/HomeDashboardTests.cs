using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Identity;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The home dashboard shows ONLY your own characters' DPS — the self trackers — never the remote/global ones the old
/// landing broadcast. A remote fleet member appearing in the global tracker list must not leak onto the home.
/// </summary>
public class HomeDashboardTests
{
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public void MyCharacters_AreOnlyTheSelfTrackers_NotRemoteMembers()
    {
        var trackers = new ObservableCollection<DpsViewModel>
        {
            new("Noahmarr", isSelf: true),
            new("RaymondKrah", isSelf: false)
        };

        var home = new HomeDashboardViewModel(new NoServices(), trackers);

        var mine = Assert.Single(home.MyCharacters);
        Assert.Equal("Noahmarr", mine.Character);
        Assert.True(home.HasCharacters);
    }

    [Fact]
    public void RemoteMemberJoining_DoesNotLeakOntoTheHome_ButMyOwnAltDoes()
    {
        var trackers = new ObservableCollection<DpsViewModel> { new("Noahmarr", isSelf: true) };
        var home = new HomeDashboardViewModel(new NoServices(), trackers);

        trackers.Add(new DpsViewModel("Catbank", isSelf: false));   // someone else connected → stays off the home
        Assert.Single(home.MyCharacters);

        trackers.Add(new DpsViewModel("Jithran", isSelf: true));    // another of my own characters → shows
        Assert.Equal(2, home.MyCharacters.Count);
    }

    [Fact]
    public async Task IdleCharacters_AppearAsGreyedRows_AlongsideLiveTrackers()
    {
        var trackers = new ObservableCollection<DpsViewModel> { new("Noahmarr", isSelf: true) };
        var registry = new StubRegistry([new Character("Noahmarr", 1), new Character("Jithran", 2), new Character("Lionear", 3)]);
        var home = new HomeDashboardViewModel(new RegistryOnly(registry), trackers);

        await home.RebuildRosterAsync();

        // The character with a live tracker stays live; my other registry characters show as idle placeholders.
        Assert.Equal(3, home.MyCharacters.Count);
        Assert.True(home.MyCharacters.Single(c => c.Character == "Noahmarr").IsLive);
        Assert.False(home.MyCharacters.Single(c => c.Character == "Jithran").IsLive);
        Assert.False(home.MyCharacters.Single(c => c.Character == "Lionear").IsLive);
    }

    [Fact]
    public void OnlyOfflineCharactersAreGreyed_NotOnlineOnesWithoutCombat()
    {
        // A live combat tracker is never "offline".
        Assert.False(new DpsViewModel("Live", isSelf: true) { IsLive = true, InEve = true }.ShowOffline);
        // In EVE but no combat yet → a normal (not greyed) row.
        Assert.False(new DpsViewModel("Online", isSelf: true) { IsLive = false, InEve = true }.ShowOffline);
        // Not in EVE and no combat → greyed offline row.
        Assert.True(new DpsViewModel("Offline", isSelf: true) { IsLive = false, InEve = false }.ShowOffline);
    }

    private sealed class RegistryOnly(ICharacterRegistry registry) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ICharacterRegistry) ? registry : null;
    }

    private sealed class StubRegistry(IReadOnlyList<Character> characters) : ICharacterRegistry
    {
        public Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(characters);
        public Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public event Action RegistryChanged { add { } remove { } }
    }
}
