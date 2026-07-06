namespace EveUtils.Shared.Runtime;

/// <summary>
/// Marker indicating which host (client or server) the current code runs on. Services
/// that behave differently per client/server (e.g. an ESI call locally vs. via the server) inject this
/// instead of a compile-time <c>#if</c>.
/// </summary>
public interface IRuntimeContext
{
    ExecutionHost Host { get; }
}
