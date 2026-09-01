using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

public sealed class RunParameter
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public RunParameterKey ParameterKey { get; set; }
    public string TypedValue { get; set; } = string.Empty;
    public DateTime ObservedAtUtc { get; set; }
    public Run? Run { get; set; }
}
