using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet;

namespace EveUtils.Client.Esi;

/// <summary>
/// <see cref="IFleetRosterChangeNotifier"/> over the toast layer. Compares two roster diffs and surfaces the important
/// transitions: planned pilots joining (Success), planned pilots leaving (Warning), and unplanned pilots arriving
/// (Information). Names are resolved best-effort through the cached public-ESI lookup; a handful are listed by name and
/// the rest collapsed to a count so a mass form-up doesn't spam. Singleton.
/// </summary>
public sealed class FleetRosterChangeNotifier(
    IToastService toasts,
    IExternalCharacterLookup lookup) : IFleetRosterChangeNotifier, ISingletonService
{
    private const int MaxNamesPerToast = 3;

    public async Task NotifyAsync(FleetRosterDiff? previous, FleetRosterDiff current, CancellationToken cancellationToken = default)
    {
        if (previous is null)
            return; // first observation of this fleet — establish a baseline silently

        await ToastTransitionAsync(Difference(current.Present, previous.Present), "joined the fleet", ToastKind.Success, cancellationToken);
        await ToastTransitionAsync(Difference(previous.Present, current.Present), "left the fleet", ToastKind.Warning, cancellationToken);
        await ToastTransitionAsync(Difference(current.External, previous.External), "joined — not in the plan", ToastKind.Information, cancellationToken);
    }

    private async Task ToastTransitionAsync(IReadOnlyList<int> ids, string verb, ToastKind kind, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;
        if (ids.Count == 1)
        {
            toasts.Show($"{await NameAsync(ids[0], cancellationToken)} {verb}", null, kind);
            return;
        }
        toasts.Show($"{ids.Count} pilots {verb}", await NamesAsync(ids, cancellationToken), kind);
    }

    private async Task<string> NameAsync(int characterId, CancellationToken cancellationToken)
    {
        var info = await lookup.LookupAsync(characterId, cancellationToken);
        return info is { Exists: true, Name.Length: > 0 } ? info.Name : $"Pilot {characterId}";
    }

    private async Task<string> NamesAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var id in ids.Take(MaxNamesPerToast))
            names.Add(await NameAsync(id, cancellationToken));
        var extra = ids.Count - names.Count;
        return extra > 0 ? $"{string.Join(", ", names)} +{extra} more" : string.Join(", ", names);
    }

    private static IReadOnlyList<int> Difference(IReadOnlyList<int> from, IReadOnlyList<int> subtract)
    {
        if (from.Count == 0)
            return [];
        var exclude = subtract as ISet<int> ?? new HashSet<int>(subtract);
        return from.Where(id => !exclude.Contains(id)).ToList();
    }
}
