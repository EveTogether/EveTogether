namespace EveUtils.Shared.Modules.Dogma;

/// <summary>Why a module activation was refused, so the UI can show the matching in-game-style reason.</summary>
public enum ModuleActivationReason
{
    /// <summary>A cloak and another active module cannot run at the same time (EVE's cloak mutual exclusion).</summary>
    CloakMutualExclusion
}
