using EveUtils.Client.Fleet;
using EveUtils.Client.Killmails;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>How LINKED LOSS and FLEET KILLS name the players on a killmail: the one ET-336 resolution
/// (<see cref="KillmailNames"/>), with the SDE for NPC corporations and factions.</summary>
internal static class DetailKillmailNames
{
    // Every dependency is optional, like the rest of the detail sections (RunDetailSectionServices' own rule): missing
    // any one of them means no live player-name resolution, not a crash — callers fall back to the bare id.
    public static KillmailNames? Build(RunDetailSectionServices services)
    {
        if (services.Services is not { } provider || services.Sde is not { } sde)
        {
            return null;
        }

        IEsiAffiliationResolver? affiliation = provider.GetService<IEsiAffiliationResolver>();
        IKillmailEntityNameRepository? repository = provider.GetService<IKillmailEntityNameRepository>();
        ISettingRepository? settings = provider.GetService<ISettingRepository>();
        if (affiliation is null || repository is null || settings is null)
        {
            return null;
        }

        Dictionary<int, string> ownNames = (services.OwnCharacterIds ?? new HashSet<long>())
            .ToDictionary(id => (int)id, id => services.NameOf?.Invoke(id) ?? id.ToString());
        return new KillmailNames(ownNames, provider.GetService<IExternalCharacterLookup>(), affiliation, sde, repository,
            settings, provider.GetService<TimeProvider>() ?? TimeProvider.System);
    }

    // A player through KillmailNames (hydrated by the caller), an NPC corporation or faction from the SDE — the same
    // split KillmailsOverviewViewModel draws (ET-332).
    public static string FinalBlowText(KillmailFinalBlowDto finalBlow, KillmailNames? names, ISdeAccessor? sde)
    {
        string who = finalBlow switch
        {
            { CharacterId: { } character } => names?.NameOf(character) ?? $"character {character}",
            { CorporationId: { } corporation } =>
                names?.NameOf(corporation) ?? sde?.GetNpcCorporationName(corporation) ?? $"corporation {corporation}",
            { FactionId: { } faction } => sde?.GetFactionName(faction) ?? $"faction {faction}",
            _ => "unknown"
        };
        return finalBlow.ShipTypeId is { } ship ? $"{who} in {TypeName(sde, ship)}" : who;
    }

    public static string TypeName(ISdeAccessor? sde, int typeId) => sde?.GetType(typeId)?.Name ?? $"type {typeId}";
}
