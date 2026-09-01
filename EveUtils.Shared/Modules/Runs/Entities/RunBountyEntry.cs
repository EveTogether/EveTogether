namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunBountyEntry
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public decimal Isk { get; set; }
    public Run? Run { get; set; }
}
