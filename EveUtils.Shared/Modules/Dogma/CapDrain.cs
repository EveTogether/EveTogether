namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// One capacitor drain (or fill) feeding the <see cref="CapacitorSimulator"/>: a module activating every
/// <see cref="Duration"/> ms (plus <see cref="ReloadTime"/> after every <see cref="ClipSize"/> activations, 0 = no
/// reload) for <see cref="CapNeed"/> GJ. A negative <see cref="CapNeed"/> is a fill (cap injector). Turrets set
/// <see cref="DisableStagger"/> so their activations are not spread out.
/// </summary>
public sealed record CapDrain(
    double Duration,
    double CapNeed,
    int ClipSize,
    bool DisableStagger,
    double ReloadTime,
    bool IsInjector);
