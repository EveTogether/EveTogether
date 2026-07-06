namespace EveUtils.Shared.Modules.Sde.Fighters;

/// <summary>The three fighter classes. Determines which launch-tube limit a squadron counts against (light → 2217,
/// support → 2218, heavy → 2219) and which platforms may launch it.</summary>
public enum FighterKind
{
    Light,
    Support,
    Heavy
}
