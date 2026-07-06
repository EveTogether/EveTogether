namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The fixed effectCategory -&gt; activation-state mapping (design §0 kernel #5). An effect applies only when its
/// host item's state reaches the state its category implies (passive always on, online/active/overload gated).
/// Categories outside the four fitting states (target/area/system/...) are not self-stat effects and do
/// not apply.
/// </summary>
public static class EffectActivation
{
    /// <summary>The minimum <see cref="ModuleState"/> at which an effect of this category is active, or null for a
    /// category that is not a state-gated self-fitting effect.</summary>
    public static ModuleState? RequiredState(int effectCategoryId) => effectCategoryId switch
    {
        0 => ModuleState.Passive,    // passive: always on
        4 => ModuleState.Online,
        1 => ModuleState.Active,
        5 => ModuleState.Overload,
        _ => null
    };

    public static bool IsActiveAt(int effectCategoryId, ModuleState state) =>
        RequiredState(effectCategoryId) is { } required && state >= required;
}
