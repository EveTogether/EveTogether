namespace EveUtils.Shared.Modules.Ships.Entities;

/// <summary>EF entity — <b>internal</b> to the Ships module. Never leaves the module; DTOs go out.</summary>
public class Ship
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public decimal Mass { get; set; }
    public List<Fitting> Fittings { get; set; } = [];
}
