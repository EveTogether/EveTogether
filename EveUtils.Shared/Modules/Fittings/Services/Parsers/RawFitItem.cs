namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// One parsed line/entry before SDE resolution. EFT carries a module <see cref="Name"/> (+ optional
/// <see cref="ChargeName"/>); DNA carries a resolved <see cref="TypeId"/>. The assembler turns this into ESI
/// fitting items with the right slot flags. <see cref="Section"/> is the blank-line-separated EFT section index and
/// <see cref="ExplicitQuantity"/> records whether the line carried an <c>xN</c> suffix — together they let the
/// assembler tell a trailing drones/cargo section from the leading slot racks (DNA leaves both at their defaults).
/// </summary>
internal sealed record RawFitItem(
    string? Name,
    int? TypeId,
    int Quantity,
    string? ChargeName,
    int Section = 0,
    bool ExplicitQuantity = false);
