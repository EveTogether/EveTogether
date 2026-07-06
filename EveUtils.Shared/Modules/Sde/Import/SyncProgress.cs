namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// A synchronous <see cref="IProgress{T}"/> that invokes the handler inline. Unlike <see cref="Progress{T}"/>
/// it does not post to a captured <see cref="SynchronizationContext"/>, so progress reports keep their order
/// (important when forwarding download bytes that must arrive monotonically).
/// </summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
