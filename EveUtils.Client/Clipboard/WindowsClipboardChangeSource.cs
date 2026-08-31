using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Windows clipboard notification: a message-only window registered with <c>AddClipboardFormatListener</c>,
/// raising <see cref="Changed"/> on every <c>WM_CLIPBOARDUPDATE</c>. The OS pushes the change, so there is no
/// interval to tune and nothing to fight over with a clipboard manager.
///
/// The window lives on its own thread with its own message pump: the Avalonia dispatcher's loop belongs to the
/// UI and a window created on the UI thread would deliver its messages there.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardChangeSource : IClipboardChangeSource
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;

    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();

    // Handed to unmanaged code as a function pointer, so a field has to keep it alive for the window's lifetime.
    private readonly WindowProcedure _procedure;

    private string? _className;
    private Thread? _pump;
    private IntPtr _window;

    public WindowsClipboardChangeSource() => _procedure = OnMessage;

    public bool IsSupported => true;

    public event Action? Changed;

    // Windows answers this on construction and never changes its mind, so this is declared and never raised.
    public event Action? SupportChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_pump is not null)
                return;

            using var ready = new ManualResetEventSlim();
            var pump = new Thread(() => RunPump(ready))
            {
                IsBackground = true,
                Name = "clipboard-listener"
            };
            pump.Start();
            ready.Wait();

            // A window that never came up leaves nothing to pump: let the thread end rather than hold a dead one.
            _pump = _window == IntPtr.Zero ? null : pump;
        }
    }

    public void Stop()
    {
        Thread? pump;
        IntPtr window;
        lock (_gate)
        {
            pump = _pump;
            window = _window;
            _pump = null;
            _window = IntPtr.Zero;
        }

        if (pump is null)
            return;

        PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        pump.Join(StopTimeout);
    }

    public void Dispose() => Stop();

    private void RunPump(ManualResetEventSlim ready)
    {
        try
        {
            _window = CreateListenerWindow();
            if (_window != IntPtr.Zero)
                AddClipboardFormatListener(_window);
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
            // Unique per instance: a class name can only be registered once per process, and a start/stop cycle
            // must not trip over the registration its predecessor left behind.
            var name = "EveTogether.ClipboardListener." + Guid.NewGuid().ToString("N");
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

        return CreateWindowEx(0, _className, "EVE Together clipboard listener", 0, 0, 0, 0, 0,
            MessageOnlyParent, IntPtr.Zero, module, IntPtr.Zero);
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_CLIPBOARDUPDATE:
                try
                {
                    Changed?.Invoke();
                }
                catch
                {
                    // A throwing subscriber must not take the pump down with it — that would silently stop every
                    // later notification with no way back short of a restart.
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                RemoveClipboardFormatListener(window);
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

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string? name);
}
