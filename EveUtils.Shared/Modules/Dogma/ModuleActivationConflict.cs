namespace EveUtils.Shared.Modules.Dogma;

/// <summary>A refused activation: the already-active module that blocks it, and why.</summary>
public sealed record ModuleActivationConflict(int BlockingTypeId, ModuleActivationReason Reason);
