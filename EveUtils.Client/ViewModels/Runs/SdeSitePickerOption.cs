using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Clipboard;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One row in a site picker (<see cref="ManualRunStartViewModel"/>, <see cref="EscalationDialogViewModel"/>): the
/// site the row picks, and a label that distinguishes it from any other row sharing its name. The single
/// presentation both pickers use, so nobody rebuilds it — the previous absence of any such rule is what let two
/// identically-named sites of different archetypes show up as unpickable duplicates (Raymond, 2026-09-05).
///
/// Note for whoever reads this later: a run saved before <see cref="SdeSiteCanonicalization"/> existed may carry
/// the other half of a twin pair's id, and will not be counted under the id twins now canonicalise to. That is a
/// one-time consequence of today's choice, not something this type corrects — see the ET-125 comment thread.
/// </summary>
public sealed record SdeSitePickerOption(SdeSite Site, string Label)
{
    /// <summary>
    /// One row per genuinely distinct site — catalogue "twins" already collapsed to their canonical row by
    /// <see cref="SdeSiteCanonicalization.Canonicalize"/>, so a pick here always carries the same dungeonId every
    /// client would pick for the same twin. A row still carries its own <see cref="SdeSiteDescription.DescribeOne"/>
    /// label, which is what tells two genuinely different sites sharing a name apart (Sansha's Command Relay
    /// Outpost: <c>2251</c> a Combat Site, <c>2406</c> an Escalation — not a twin, so it stays two rows).
    /// </summary>
    public static IReadOnlyList<SdeSitePickerOption> From(IReadOnlyList<SdeSite> sites) =>
        [.. SdeSiteCanonicalization.Canonicalize(sites)
            .Select(site => SdeSiteDescription.DescribeOne(site) is { Length: > 0 } facts
                ? new SdeSitePickerOption(site, $"{site.Name} — {facts}")
                : new SdeSitePickerOption(site, site.Name))];
}
