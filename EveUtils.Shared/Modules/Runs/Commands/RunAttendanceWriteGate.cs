using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>One attendance write at a time per client, so two windows on the same run cannot collide on the Sqlite
/// file. A singleton rather than a static, so it is shared by one client's windows and never by two clients.</summary>
[ClientOnly]
internal sealed class RunAttendanceWriteGate : ISingletonService
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}
