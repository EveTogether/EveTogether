using EveUtils.Client.Notifications;
using EveUtils.Client.Opsec;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Runs;

/// <summary>
/// A copied signature or mission leaves a running freerun alone (ET-508: the freerun goes on, the copy is ignored).
/// Decided before any character question or window comes up, so the copy changes nothing at all. Only the copier's own
/// freerun counts: a second character copying a site while the first one roams still gets its run. A copy whose
/// sender is not known is treated as the freerunner's — a site start is easier to redo than a roam.
/// </summary>
public static class FreerunCopyGuard
{
    /// <summary>True when the copy is to be ignored. The pilot is told so: nothing else on screen would show it.</summary>
    public static async Task<bool> IgnoresCopyAsync(IServiceProvider services, IToastService toasts,
        string? copiedByCharacter, string what)
    {
        if (services.GetService<IDispatcher>() is not { } dispatcher)
            return false;

        Result<IReadOnlyList<RunningRunDto>> running = await dispatcher.Query(new GetRunningRunsQuery());
        IReadOnlyList<RunningRunDto> freeruns = running.IsSuccess
            ? [.. (running.Value ?? []).Where(run =>
                RunTypeCatalogue.For(run.ActivityKind, run.SignatureGroupSnapshot, run.SiteTypeId).IgnoresCopiedSites)]
            : [];
        if (freeruns.Count == 0)
            return false;

        int? copierId = copiedByCharacter is not null && services.GetService<ICharacterRegistry>() is { } registry
            ? (await registry.GetAllAsync()).FirstOrDefault(character =>
                string.Equals(character.Name, copiedByCharacter, StringComparison.OrdinalIgnoreCase))?.EsiCharacterId
            : null;
        RunningRunDto? kept = copierId is { } id
            ? freeruns.FirstOrDefault(run => run.CharacterId == id)
            : freeruns[0];
        if (kept is null)
            return false;

        Tell(toasts, kept.SiteName, what);
        return true;
    }

    public static void Tell(IToastService? toasts, string? freerunName, string what) =>
        toasts?.Show("Copy ignored", $"{OpsecText.Mark(freerunName)} goes on, so {what} did not start a run.",
            ToastKind.Information);
}
