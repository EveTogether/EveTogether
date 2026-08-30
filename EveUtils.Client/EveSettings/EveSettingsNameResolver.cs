using EveUtils.Client.Fleet;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// Builds an <see cref="EveSettingsNames"/> for a profile: the character registry first, public ESI for the ids it
/// does not know, the account names the user gave, and which characters sit on which account. Every step is
/// best-effort — an unreachable ESI costs a name, never the tool.
///
/// The account links are read across <em>every</em> profile, not just the one on screen (ET-64): a profile where the
/// user multiboxes carries no usable trace, while a quieter one beside it spells the same accounts out. Whatever is
/// established that way is written back, so it survives a later evening that would have proved nothing.
/// </summary>
public sealed class EveSettingsNameResolver(
    ICharacterRegistry registry,
    IExternalCharacterLookup lookup,
    EveSettingsPreferences preferences) : ISingletonService
{
    /// <param name="profile">The profile on screen — whose files get named.</param>
    /// <param name="allProfiles">
    /// Every profile under the install directory, for the account links. Pass just <paramref name="profile"/> when
    /// there is nothing else to read.
    /// </param>
    public async Task<EveSettingsNames> ResolveAsync(
        EveSettingsProfile profile,
        IReadOnlyList<EveSettingsProfile>? allProfiles = null,
        CancellationToken cancellationToken = default)
    {
        var profiles = allProfiles is { Count: > 0 } ? allProfiles : [profile];
        var characters = new Dictionary<long, string>();

        foreach (var known in await registry.GetAllAsync(cancellationToken))
        {
            if (known.EsiCharacterId is { } id)
                characters[id] = known.Name;
        }

        // Everything the registry does not know — an alt that was never added here: public ESI, served from its
        // day-long cache after the first open, so re-opening the tool costs no calls at all. Across all profiles,
        // because an account's characters can be named from a profile other than the one being shown.
        foreach (var file in profiles.SelectMany(other => other.Characters).DistinctBy(file => file.Id))
        {
            if (characters.ContainsKey(file.Id) || file.Id is <= 0 or > int.MaxValue)
                continue;

            var info = await lookup.LookupAsync((int)file.Id, cancellationToken);
            if (info.Exists && !string.IsNullOrWhiteSpace(info.Name))
                characters[file.Id] = info.Name;
        }

        var stored = await preferences.LoadAccountLinksAsync(cancellationToken);
        var (links, changed) = AccountLinkStore.Merge(
            stored, EveSettingsLocator.DeriveAccountLinks(profiles), DateTimeOffset.UtcNow);
        if (changed)
            await preferences.SaveAccountLinksAsync(links.Values, cancellationToken);

        return new EveSettingsNames(
            characters,
            await preferences.LoadAccountNamesAsync(cancellationToken),
            links);
    }
}
