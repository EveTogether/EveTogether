namespace EveUtils.Shared.Runtime;

/// <summary>Where the code is currently running. For host-dependent behavior without <c>#if</c>.</summary>
public enum ExecutionHost
{
    Client,
    Server
}
