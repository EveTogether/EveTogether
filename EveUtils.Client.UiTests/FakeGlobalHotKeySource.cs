using Avalonia.Input;
using EveUtils.Client.Input;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.UiTests;

/// <summary>Fake standing in for the real Win32 registration — records what would have been claimed/released
/// instead of ever touching a live OS hotkey from a test.</summary>
internal sealed class FakeGlobalHotKeySource : IGlobalHotKeySource
{
    public bool IsSupported { get; set; } = true;
    public int RegisterCalls { get; private set; }
    public int UnregisterCalls { get; private set; }
    public KeyGesture? RegisteredGesture { get; private set; }
    public Func<KeyGesture, Result>? OnRegister { get; set; }

    public event Action? Pressed;

    public Result Register(KeyGesture gesture)
    {
        RegisterCalls++;
        var result = OnRegister?.Invoke(gesture) ?? Result.Success();
        RegisteredGesture = result.IsSuccess ? gesture : null;
        return result;
    }

    public void Unregister()
    {
        UnregisterCalls++;
        RegisteredGesture = null;
    }

    public void RaisePressed() => Pressed?.Invoke();

    public void Dispose()
    {
    }
}
