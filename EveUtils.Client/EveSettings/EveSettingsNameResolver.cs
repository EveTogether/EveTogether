using EveUtils.Client.Fleet;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Builds an <see cref="EveSettingsNames"/> for a profile: the character registry first, public ESI for the ids it
/// does not know, and the account names the user gave. Every step is best-effort — an unreachable ESI costs a name,
/// never the tool.
/// </summary>
public sealed class EveSettingsNameResolver(
    ICharacterRegistry registry,
    IExternalCharacterLookup lookup,
    EveSettingsPreferences preferences) : ISingletonService
{
    public async Task<EveSettingsNames> ResolveAsync(
        EveSettingsProfile profile, CancellationToken cancellationToken = default)
    {
        var characters = new Dictionary<long, string>();

        foreach (var known in await registry.GetAllAsync(cancellationToken))
        {
            if (known.EsiCharacterId is { } id)
                characters[id] = known.Name;
        }

        // Everything the registry does not know — an alt that was never added here: public ESI, served from its
        // day-long cache after the first open, so re-opening the tool costs no calls at all.
        foreach (var file in profile.Characters)
        {
            if (characters.ContainsKey(file.Id) || file.Id is <= 0 or > int.MaxValue)
                continue;

            var info = await lookup.LookupAsync((int)file.Id, cancellationToken);
            if (info.Exists && !string.IsNullOrWhiteSpace(info.Name))
                characters[file.Id] = info.Name;
        }

        return new EveSettingsNames(
            characters,
            await preferences.LoadAccountNamesAsync(cancellationToken),
            EveSettingsLocator.AccountCharacterHints(profile));
    }
}
