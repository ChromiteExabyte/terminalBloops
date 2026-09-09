using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TerminalBloops.Core;

namespace TerminalBloops.App.Capture;

/// <summary>Observes terminal HWNDs on the current interactive desktop, without injecting code.</summary>
public sealed class WindowObserver : IDisposable
{
    private const uint ObjectCreate = 0x8000, ObjectDestroy = 0x8001, ObjectShow = 0x8002, ObjectHide = 0x8003;
    private const uint ObjectCloaked = 0x8017, ObjectUncloaked = 0x8018, Foreground = 0x0003;
    private const uint ConsoleStart = 0x4006, ConsoleEnd = 0x4007;
    private const uint QuitMessage = 0x0012;
    private readonly WinEventCallback _callback;
    private readonly Dictionary<nint, WindowState> _windows = [];
    private readonly List<nint> _hooks = [];
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _disposed;

    public event Action<WindowObservation>? ObservationReceived;
    public event Action<CaptureNotice>? Notice;

    public WindowObserver() => _callback = OnWinEvent;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) throw new InvalidOperationException("Window observation has already started.");
        _thread = new Thread(Run) { IsBackground = true, Name = "Terminal Bloops window observer" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            Report("window-observer-unavailable", "The window observer did not finish starting. Process recording can continue independently.");
    }

    private void Run()
    {
        try
        {
            // Ensure the thread owns a message queue before publishing its ID for shutdown.
            PeekMessage(out _, 0, 0, 0, 0);
            _threadId = GetCurrentThreadId();
            if (_disposed) return;
            Hook(ObjectCreate, ObjectHide);
            Hook(ObjectCloaked, ObjectUncloaked);
            Hook(Foreground, Foreground);
            Hook(ConsoleStart, ConsoleEnd);

            // Existing windows are cached silently. Their creation time is not known.
            EnumWindows((hwnd, _) =>
            {
                var state = ReadWindow(hwnd);
                if (state is not null)
                {
                    state.IsShown = IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsCloaked(hwnd);
                    _windows[hwnd] = state;
                }
                return true;
            }, 0);
            Report("window-observer-ready", "Terminal windows are being observed on this desktop.");
            _ready.Set();
            int result;
            while (!_disposed && (result = GetMessage(out var message, 0, 0, 0)) != 0)
            {
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                TranslateMessage(in message);
                DispatchMessage(in message);
            }
        }
        catch (Exception ex) { Report("window-observer-unavailable", $"Window observation stopped: {ex.Message}"); }
        finally
        {
            foreach (var hook in _hooks) UnhookWinEvent(hook);
            _hooks.Clear();
            _windows.Clear();
            _ready.Set();
        }
    }

    private void Hook(uint minimum, uint maximum)
    {
        // OUTOFCONTEXT is 0; SKIPOWNPROCESS is 2. No DLL enters another process.
        var hook = SetWinEventHook(minimum, maximum, 0, _callback, 0, 0, 2);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register a window event hook.");
        _hooks.Add(hook);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId,
        uint eventThread, uint eventMilliseconds)
    {
        if (_disposed || hwnd == 0) return;
        try
        {
            var timestamp = FromEventMilliseconds(eventMilliseconds);
            if (eventType is ConsoleStart or ConsoleEnd)
            {
                HandleConsoleEvent(eventType, hwnd, objectId, timestamp);
                return;
            }
            // OBJID_WINDOW and CHILDID_SELF; ignore text, cursor, and accessibility child events.
            if (objectId != 0 || childId != 0) return;

            if (eventType is ObjectDestroy or ObjectHide or ObjectCloaked)
            {
                if (_windows.TryGetValue(hwnd, out var closing))
                {
                    if (closing.IsShown)
                    {
                        closing.IsShown = false;
                        Emit(closing, timestamp, isClosed: true);
                    }
                    if (eventType == ObjectDestroy)
                    {
                        _windows.Remove(hwnd);
                    }
                }
                return;
            }

            // Read creation metadata early; after a fast DESTROY, these APIs can no longer resolve the HWND.
            var current = ReadWindow(hwnd);
            if (current is null && !_windows.TryGetValue(hwnd, out current)) return;
            if (_windows.TryGetValue(hwnd, out var previous) && previous.ProcessId == current.ProcessId &&
                previous.ProcessStartUtc == current.ProcessStartUtc)
            {
                foreach (var client in previous.Clients) current.Clients.TryAdd(client.Key, client.Value);
                current.IsShown = previous.IsShown;
                if (string.IsNullOrWhiteSpace(current.Title)) current.Title = previous.Title;
            }
            _windows[hwnd] = current;
            if (eventType == ObjectCreate) return;

            if (eventType == Foreground && (!IsWindowVisible(hwnd) || IsIconic(hwnd))) return;
            if (IsCloaked(hwnd) || IsIconic(hwnd)) return;
            if (eventType is ObjectShow or ObjectUncloaked or Foreground)
            {
                // SHOW itself is evidence even if the window hid before this queued callback ran.
                // Only foreground changes require a currently visible HWND.
                var wasShown = current.IsShown;
                current.IsShown = true;
                if (!wasShown || eventType is ObjectShow or ObjectUncloaked)
                    Emit(current, timestamp, isClosed: false);
            }
        }
        catch (Exception ex) { Report("window-observation-error", $"A window event could not be read: {ex.Message}"); }
    }

    private void HandleConsoleEvent(uint eventType, nint hwnd, int clientPid, DateTimeOffset timestamp)
    {
        if (clientPid <= 0 || clientPid == Environment.ProcessId) return;
        if (!_windows.TryGetValue(hwnd, out var state))
        {
            state = ReadWindow(hwnd);
            if (state is null) return;
            _windows[hwnd] = state;
        }
        // This event's client PID is meaningful for a classic console HWND only.
        // ConPTY's message-only HWND must never be mistaken for a visible Terminal pane.
        if (!state.WindowClass.Equals("ConsoleWindowClass", StringComparison.Ordinal)) return;
        if (clientPid == state.ProcessId) return;
        if (eventType == ConsoleStart)
        {
            state.Clients[clientPid] = ReadProcessMetadata(clientPid, includeImageName: false).StartUtc;
            if (state.IsShown && IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsCloaked(hwnd))
                Emit(state, timestamp, isClosed: false);
        }
        else
        {
            // Retaining an exited client's PID could later attribute a window to a reused PID.
            // The last previously observed client keeps its established evidence in storage.
            state.Clients.Remove(clientPid);
        }
    }

    private WindowState? ReadWindow(nint hwnd)
    {
        if (GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0 ||
            processId == Environment.ProcessId || GetAncestor(hwnd, 2) != hwnd) return null;
        var classBuffer = new StringBuilder(256);
        if (GetClassName(hwnd, classBuffer, classBuffer.Capacity) == 0) return null;
        var windowClass = classBuffer.ToString();
        var classic = windowClass.Equals("ConsoleWindowClass", StringComparison.Ordinal);
        var cascadia = windowClass.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase);
        var metadata = ReadProcessMetadata((int)processId);
        var image = metadata.ImageName;
        var otherTerminal = image is "mintty.exe" or "wezterm-gui.exe" or "alacritty.exe" or "conemu.exe" or "conemu64.exe";
        if (!classic && !cascadia && !otherTerminal) return null;
        var titleBuffer = new StringBuilder(2048);
        GetWindowText(hwnd, titleBuffer, titleBuffer.Capacity);
        return new WindowState(hwnd, (int)processId, titleBuffer.ToString(), windowClass, metadata.StartUtc);
    }

    private static (string ImageName, DateTimeOffset? StartUtc) ReadProcessMetadata(int processId, bool includeImageName = true)
    {
        var process = OpenProcess(0x1000, false, (uint)processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return ("", null);
        try
        {
            DateTimeOffset? startUtc = null;
            if (GetProcessTimes(process, out var created, out _, out _, out _) && created > 0)
                startUtc = DateTimeOffset.FromFileTime(created).ToUniversalTime();
            var imageName = "";
            if (includeImageName)
            {
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (QueryFullProcessImageName(process, 0, path, ref length))
                    imageName = Path.GetFileName(path.ToString()).ToLowerInvariant();
            }
            return (imageName, startUtc);
        }
        finally { CloseHandle(process); }
    }

    private void Emit(WindowState state, DateTimeOffset timestamp, bool isClosed)
    {
        var clientIdentity = state.Clients.Count == 1 ? state.Clients.First() : default;
        int? client = state.Clients.Count == 1 ? clientIdentity.Key : null;
        ObservationReceived?.Invoke(new WindowObservation(state.ProcessId, client, timestamp, isClosed,
            state.Title, state.WindowClass, (long)state.Hwnd, true, state.ProcessStartUtc,
            client is not null ? clientIdentity.Value : null));
    }

    private static DateTimeOffset FromEventMilliseconds(uint eventMilliseconds)
    {
        // WinEvent timestamps and TickCount share the boot-relative millisecond clock. Unsigned
        // subtraction handles its 49.7-day wrap; callback latency must not extend a PID's lifetime.
        var nowUtc = DateTimeOffset.UtcNow;
        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - eventMilliseconds);
        return nowUtc.AddMilliseconds(-elapsedMilliseconds);
    }

    private static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private void Report(string kind, string message)
    {
        try { Notice?.Invoke(new CaptureNotice(DateTimeOffset.UtcNow, kind, message)); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, QuitMessage, 0, 0);
        if (_thread is { IsAlive: true } && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(3));
        GC.KeepAlive(_callback);
    }

    private sealed class WindowState(nint hwnd, int processId, string title, string windowClass, DateTimeOffset? processStartUtc)
    {
        public nint Hwnd { get; } = hwnd;
        public int ProcessId { get; } = processId;
        public string Title { get; set; } = title;
        public string WindowClass { get; } = windowClass;
        public DateTimeOffset? ProcessStartUtc { get; } = processStartUtc;
        public bool IsShown { get; set; }
        public Dictionary<int, DateTimeOffset?> Clients { get; } = [];
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventCallback(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, nint hwnd, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint hwnd, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);
    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder className, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder title, int capacity);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long creationTime, out long exitTime,
        out long kernelTime, out long userTime);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
