using System.Collections.Generic;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Carries the per-group active/online counters while a fit's modules are resolved to their default states, so the
/// group-capacity clamps (attr 763 <c>maxGroupActive</c>, attr 764 <c>maxGroupOnline</c>) can see how many modules of a
/// group already claimed a slot earlier in the fit. One instance per fit; pass it through every
/// <see cref="ModuleStateResolver.DefaultState"/> call for that fit.
/// </summary>
public sealed class ModuleStateAccumulator
{
    /// <summary>Modules of a group that already resolved to active (drives the attr-763 <c>maxGroupActive</c> clamp).</summary>
    public Dictionary<int, int> ActivePerGroup { get; } = [];

    /// <summary>Modules of a group that already resolved to online-or-higher (drives the attr-764 <c>maxGroupOnline</c> clamp).</summary>
    public Dictionary<int, int> OnlinePerGroup { get; } = [];
}
