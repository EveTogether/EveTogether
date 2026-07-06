namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>A charge type a module can load: its id, display name and charge size (attr 128, null when the
/// charge is not sized — missiles, scripts), used to match against the module's accepted charge size.</summary>
public sealed record SdeChargeType(int TypeId, string Name, double? ChargeSize);
