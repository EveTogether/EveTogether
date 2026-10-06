using EveUtils.Client.Composition;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class ClientInstanceLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "evetogether-instancelock-" + Guid.NewGuid().ToString("N"));

    public ClientInstanceLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TryAcquire_WhileAnotherClientHoldsTheDirectory_IsRefused()
    {
        using ClientInstanceLock? first = ClientInstanceLock.TryAcquire(_root);

        using ClientInstanceLock? second = ClientInstanceLock.TryAcquire(_root);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public void TryAcquire_AfterTheHolderLetGo_IsGranted()
    {
        ClientInstanceLock? first = ClientInstanceLock.TryAcquire(_root);
        Assert.NotNull(first);
        first.Dispose();

        using ClientInstanceLock? next = ClientInstanceLock.TryAcquire(_root);

        Assert.NotNull(next);
    }

    [Fact]
    public void TryAcquire_LockFileLeftBehindByAnEndedClient_IsGranted()
    {
        File.WriteAllText(Path.Combine(_root, "instance.lock"), "");
        File.WriteAllText(Path.Combine(_root, "instance.pid"), "999999");

        using ClientInstanceLock? next = ClientInstanceLock.TryAcquire(_root);

        Assert.NotNull(next);
        Assert.Equal(Environment.ProcessId, ClientInstanceLock.OwnerProcessId(_root));
    }

    [Fact]
    public void TryAcquire_InstanceWithADirectoryOfItsOwn_RunsBesideTheDefault()
    {
        string instanceDirectory = Path.Combine(_root, "B");
        Directory.CreateDirectory(instanceDirectory);
        using ClientInstanceLock? main = ClientInstanceLock.TryAcquire(_root);

        using ClientInstanceLock? instance = ClientInstanceLock.TryAcquire(instanceDirectory);

        Assert.NotNull(main);
        Assert.NotNull(instance);
    }

    [Fact]
    public async Task Acquire_HolderLetsGoWithinThePatience_IsGranted()
    {
        ClientInstanceLock? leaving = ClientInstanceLock.TryAcquire(_root);
        Assert.NotNull(leaving);
        Task letGo = Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken)
            .ContinueWith(_ => leaving.Dispose(), TestContext.Current.CancellationToken);

        using ClientInstanceLock? next = ClientInstanceLock.Acquire(_root, TimeSpan.FromSeconds(10));

        await letGo;
        Assert.NotNull(next);
    }

    [Fact]
    public void Acquire_HolderStaysPastThePatience_IsRefused()
    {
        using ClientInstanceLock? staying = ClientInstanceLock.TryAcquire(_root);

        using ClientInstanceLock? next = ClientInstanceLock.Acquire(_root, TimeSpan.FromMilliseconds(500));

        Assert.NotNull(staying);
        Assert.Null(next);
    }

    [Fact]
    public void OwnerProcessId_WhileHeld_IsTheHoldingProcess()
    {
        using ClientInstanceLock? held = ClientInstanceLock.TryAcquire(_root);

        Assert.NotNull(held);
        Assert.Equal(Environment.ProcessId, ClientInstanceLock.OwnerProcessId(_root));
    }

    [Fact]
    public void OwnerProcessId_NothingEverHeldTheDirectory_IsUnknown()
    {
        Assert.Null(ClientInstanceLock.OwnerProcessId(_root));
    }
}
