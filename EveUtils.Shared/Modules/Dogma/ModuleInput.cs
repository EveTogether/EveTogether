namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A fitted module in a calculation request: its type, its activation state and (reserved) the loaded charge.
/// Charges are not yet instantiated, so an <c>OtherID</c> modifier finds no charge and is skipped (V-4).
/// </summary>
public sealed record ModuleInput(int TypeId, ModuleState State, int? ChargeTypeId = null);
