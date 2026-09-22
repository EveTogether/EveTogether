using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Avalonia.Input;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.Input;

/// <summary>
/// Windows system-wide hotkey (ET-320): a message-only window on its own thread with its own message pump — the
/// same shape as <see cref="Clipboard.WindowsClipboardChangeSource"/>, for the same reason. <c>RegisterHotKey</c> /
/// <c>UnregisterHotKey</c> are called on that thread via <c>SendMessage</c>, mirroring how the clipboard listener
/// calls <c>AddClipboardFormatListener</c> from the thread that owns the window receiving its notifications —
/// rather than from whichever thread happens to ask.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotKeySource : IGlobalHotKeySource
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP = 0x8000;
    private const uint WM_REGISTER = WM_APP + 1;
    private const uint WM_UNREGISTER = WM_APP + 2;

    private const int HotKeyId = 1;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    // Win32 virtual-key codes for exactly the keys KeyboardShortcutRegistry.IsRecordable allows to be recorded
    // (any key held with a modifier, or a bare function key/Escape) — named by the Avalonia Key so no numeric
    // assumption about that enum's own layout is needed.
    private static readonly Dictionary<Key, uint> VirtualKeyCodes = new()
    {
        [Key.A] = 0x41, [Key.B] = 0x42, [Key.C] = 0x43, [Key.D] = 0x44, [Key.E] = 0x45, [Key.F] = 0x46,
        [Key.G] = 0x47, [Key.H] = 0x48, [Key.I] = 0x49, [Key.J] = 0x4A, [Key.K] = 0x4B, [Key.L] = 0x4C,
        [Key.M] = 0x4D, [Key.N] = 0x4E, [Key.O] = 0x4F, [Key.P] = 0x50, [Key.Q] = 0x51, [Key.R] = 0x52,
        [Key.S] = 0x53, [Key.T] = 0x54, [Key.U] = 0x55, [Key.V] = 0x56, [Key.W] = 0x57, [Key.X] = 0x58,
        [Key.Y] = 0x59, [Key.Z] = 0x5A,
        [Key.D0] = 0x30, [Key.D1] = 0x31, [Key.D2] = 0x32, [Key.D3] = 0x33, [Key.D4] = 0x34,
        [Key.D5] = 0x35, [Key.D6] = 0x36, [Key.D7] = 0x37, [Key.D8] = 0x38, [Key.D9] = 0x39,
        [Key.F1] = 0x70, [Key.F2] = 0x71, [Key.F3] = 0x72, [Key.F4] = 0x73, [Key.F5] = 0x74, [Key.F6] = 0x75,
        [Key.F7] = 0x76, [Key.F8] = 0x77, [Key.F9] = 0x78, [Key.F10] = 0x79, [Key.F11] = 0x7A, [Key.F12] = 0x7B,
        [Key.F13] = 0x7C, [Key.F14] = 0x7D, [Key.F15] = 0x7E, [Key.F16] = 0x7F, [Key.F17] = 0x80, [Key.F18] = 0x81,
        [Key.F19] = 0x82, [Key.F20] = 0x83, [Key.F21] = 0x84, [Key.F22] = 0x85, [Key.F23] = 0x86, [Key.F24] = 0x87,
        [Key.Escape] = 0x1B, [Key.Tab] = 0x09, [Key.Back] = 0x08, [Key.Return] = 0x0D, [Key.Space] = 0x20,
        [Key.Insert] = 0x2D, [Key.Delete] = 0x2E, [Key.Home] = 0x24, [Key.End] = 0x23,
        [Key.PageUp] = 0x21, [Key.PageDown] = 0x22, [Key.Up] = 0x26, [Key.Down] = 0x28, [Key.Left] = 0x25, [Key.Right] = 0x27,
        [Key.OemComma] = 0xBC, [Key.OemPeriod] = 0xBE, [Key.OemQuestion] = 0xBF, [Key.OemSemicolon] = 0xBA,
        [Key.OemQuotes] = 0xDE, [Key.OemOpenBrackets] = 0xDB, [Key.OemCloseBrackets] = 0xDD, [Key.OemPipe] = 0xDC,
        [Key.OemMinus] = 0xBD, [Key.OemPlus] = 0xBB, [Key.OemTilde] = 0xC0,
    };

    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly WindowProcedure _procedure;

    private string? _className;
    private Thread? _pump;
    private IntPtr _window;
    private bool _registered;

    public WindowsGlobalHotKeySource() => _procedure = OnMessage;

    public bool IsSupported => true;

    public event Action? Pressed;

    public Result Register(KeyGesture gesture)
    {
        if (!VirtualKeyCodes.TryGetValue(gesture.Key, out var virtualKey))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.HotKeyUnavailable,
                $"{gesture} cannot be claimed as a global shortcut.", "Shortcuts"));

        if (!_EnsurePumpStarted())
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.HotKeyUnavailable,
                "The global shortcut listener could not start.", "Shortcuts"));

        var modifiers = MOD_NOREPEAT
            | (gesture.KeyModifiers.HasFlag(KeyModifiers.Control) ? MOD_CONTROL : 0)
            | (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift) ? MOD_SHIFT : 0)
            | (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt) ? MOD_ALT : 0)
            | (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? MOD_WIN : 0);

        // Always released first: re-registering the same id on the same window just fails otherwise, and a rebind
        // in Settings has to replace the old combination, not be refused by it.
        _SendToPump(WM_UNREGISTER, IntPtr.Zero, IntPtr.Zero);
        var succeeded = _SendToPump(WM_REGISTER, new IntPtr((int)modifiers), new IntPtr((int)virtualKey)) != IntPtr.Zero;
        _registered = succeeded;

        return succeeded ? Result.Success() : Result.Failure(new ResultMessage(MessageSeverity.Error,
            MessageCodes.HotKeyUnavailable,
            $"{gesture} is already claimed by another program, so it will only work while this window has focus.",
            "Shortcuts"));
    }

    public void Unregister()
    {
        if (!_registered) return;
        _SendToPump(WM_UNREGISTER, IntPtr.Zero, IntPtr.Zero);
        _registered = false;
    }

    public void Dispose()
    {
        Thread? pump;
        IntPtr window;
        lock (_gate)
        {
            pump = _pump;
            window = _window;
            _pump = null;
            _window = IntPtr.Zero;
            _registered = false;
        }

        if (pump is null)
            return;

        PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        pump.Join(StopTimeout);
    }

    private bool _EnsurePumpStarted()
    {
        lock (_gate)
        {
            if (_pump is not null)
                return true;

            using var ready = new ManualResetEventSlim();
            var pump = new Thread(() => RunPump(ready))
            {
                IsBackground = true,
                Name = "global-hotkey-listener"
            };
            pump.Start();
            ready.Wait();

            _pump = _window == IntPtr.Zero ? null : pump;
            return _pump is not null;
        }
    }

    private IntPtr _SendToPump(uint message, IntPtr wParam, IntPtr lParam)
    {
        IntPtr window;
        lock (_gate)
            window = _window;
        return window == IntPtr.Zero ? IntPtr.Zero : SendMessage(window, message, wParam, lParam);
    }

    private void RunPump(ManualResetEventSlim ready)
    {
        try
        {
            _window = CreateListenerWindow();
        }
        finally
        {
            ready.Set();
        }

        if (_window == IntPtr.Zero)
            return;

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            DispatchMessage(ref message);
    }

    private IntPtr CreateListenerWindow()
    {
        var module = GetModuleHandle(null);

        if (_className is null)
        {
            // Unique per instance, same reasoning as the clipboard listener's own class name: a start/stop cycle
            // must not trip over a registration its predecessor left behind.
            var name = "EveTogether.GlobalHotKeyListener." + Guid.NewGuid().ToString("N");
            var windowClass = new WindowClass
            {
                cbSize = (uint)Marshal.SizeOf<WindowClass>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procedure),
                hInstance = module,
                lpszClassName = name
            };
            if (RegisterClassEx(ref windowClass) == 0)
                return IntPtr.Zero;
            _className = name;
        }

        return CreateWindowEx(0, _className, "EVE Together global hotkey listener", 0, 0, 0, 0, 0,
            MessageOnlyParent, IntPtr.Zero, module, IntPtr.Zero);
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_HOTKEY:
                try
                {
                    Pressed?.Invoke();
                }
                catch
                {
                    // A throwing subscriber must not take the pump down with it — the same rule the clipboard
                    // listener holds for its own Changed event.
                }
                return IntPtr.Zero;

            case WM_REGISTER:
                UnregisterHotKey(window, HotKeyId); // clears a stale claim before asking for a fresh one
                return RegisterHotKey(window, HotKeyId, (uint)wParam.ToInt32(), (uint)lParam.ToInt32())
                    ? new IntPtr(1) : IntPtr.Zero;

            case WM_UNREGISTER:
                UnregisterHotKey(window, HotKeyId);
                return IntPtr.Zero;

            case WM_DESTROY:
                UnregisterHotKey(window, HotKeyId);
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Message message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string? name);
}
