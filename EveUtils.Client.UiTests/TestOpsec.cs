using EveUtils.Client.Opsec;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The OPSEC text hook every headless view in this assembly draws through, as <c>App</c> installs it for the real client
/// (ET-417): marked text reaches a text block stripped, the way a player sees it with OPSEC off, or masked inside an
/// <see cref="On"/> scope. One for the whole process, because the hook is a class handler on every text block.
/// </summary>
internal static class TestOpsec
{
    private static OpsecTextRenderer? _renderer;

    public static FakeOpsecService Service { get; } = new();

    public static void Install() => _renderer ??= new OpsecTextRenderer(Service);

    /// <summary>OPSEC on until the scope ends; switched off again even when the test fails.</summary>
    public static IDisposable On()
    {
        Service.Set(true);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Service.Set(false);
    }
}
