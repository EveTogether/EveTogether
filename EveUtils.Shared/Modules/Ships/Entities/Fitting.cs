namespace EveUtils.Shared.Modules.Ships.Entities;

public class Fitting
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ShipId { get; set; }
    public Ship Ship { get; set; } = default!;
}
