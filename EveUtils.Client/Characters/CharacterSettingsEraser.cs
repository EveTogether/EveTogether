using EveUtils.Client.Fleet;
using EveUtils.Client.Runs;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Settings.Repositories;

namespace EveUtils.Client.Characters;

/// <summary>
/// The client settings filed under a removed character's id (ET-345): its remembered own-toon pick and its per-fleet
/// sharing overrides. The fit-detection overrides go with <see cref="Esi.ShipFitDetectionService"/>, which also holds
/// them in memory.
/// </summary>
internal sealed class CharacterSettingsEraser(ISettingRepository settings) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        var lastPickKey = OwnCharacterPickMemory.KeyFor(characterId);
        foreach (var setting in await settings.ListAsync(cancellationToken))
            if (setting.Key == lastPickKey || MetricShareSnapshot.IsOverrideKeyOf(setting.Key, characterId))
                await settings.DeleteAsync(setting.Key, cancellationToken);
    }
}
