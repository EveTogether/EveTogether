namespace EveUtils.Shared.Runtime;

public sealed class RuntimeContext(ExecutionHost host) : IRuntimeContext
{
    public ExecutionHost Host { get; } = host;
}
