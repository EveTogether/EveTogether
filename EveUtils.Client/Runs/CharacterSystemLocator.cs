using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Runs;

public interface ICharacterSystemLocator
{
    /// <summary>The SDE id of the solar system the character is in right now, or null while no system is known yet
    /// (no gamelog jump or ESI reading so far) or the SDE does not know the name.</summary>
    int? SolarSystemIdOf(int characterId);
}

/// <summary>The same live location the fleet location sample and the run window's LOCATION section read, turned
/// into the id <c>Run.SolarSystemId</c> stores.</summary>
public sealed class CharacterSystemLocator(GamelogClientService gamelog, SolarSystemIdResolver systemIds)
    : ICharacterSystemLocator, ISingletonService
{
    public int? SolarSystemIdOf(int characterId) =>
        gamelog.LocationOf(characterId) is { } location ? systemIds.Resolve(location.System) : null;
}
