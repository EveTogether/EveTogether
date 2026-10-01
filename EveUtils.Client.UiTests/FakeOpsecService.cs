using System.Diagnostics.CodeAnalysis;
using EveUtils.Client.Opsec;

namespace EveUtils.Client.UiTests;

/// <summary>An <see cref="IOpsecService"/> switched directly by a test, with the real per-session mask: the
/// persistence half is <see cref="OpsecServiceTests"/>' to prove.</summary>
public sealed class FakeOpsecService : IOpsecService
{
    private readonly OpsecMask _mask = OpsecMask.ForThisSession();

    public bool IsEnabled { get; set; }

    public event Action? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Set(enabled);
        return Task.CompletedTask;
    }

    public void Set(bool enabled)
    {
        IsEnabled = enabled;
        Changed?.Invoke();
    }

    [return: NotNullIfNotNull(nameof(text))]
    public string? Render(string? text) =>
        IsEnabled ? OpsecText.Replace(text, _mask.Mask) : OpsecText.Strip(text);
}
