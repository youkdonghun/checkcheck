using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CheckCheck.App.Native;

/// <summary>Displays a launcher on any ordinary external right click, independent of text-provider success.</summary>
internal sealed class SelectionPopupWatcher : IDisposable
{
    private readonly NativeTextBridge _bridge;
    private readonly Dispatcher _dispatcher;
    private readonly Action<nint, CaptureSnapshot?, int, int> _show;
    private readonly Action _dismiss;
    private readonly HookProc _callback;
    private nint _hook, _selectionWindow;
    private int _generation;
    private bool _disposed;
    private long _selectedAt;
    private CaptureSnapshot? _selection;

    internal SelectionPopupWatcher(NativeTextBridge bridge, Dispatcher dispatcher, Action<nint, CaptureSnapshot?, int, int> show, Action dismiss)
    {
        _bridge = bridge; _dispatcher = dispatcher; _show = show; _dismiss = dismiss;
        _callback = Observe;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new InvalidOperationException("우클릭 감지를 시작하지 못했어요. 트레이 메뉴에서 다시 켜거나 단축키를 이용해 주세요.");
    }

    private nint Observe(int code, nint message, nint data)
    {
        if (code >= 0 && !_disposed)
        {
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            if (message == 0x0201)
            {
                _selection = null; _generation++;
                GetWindowThreadProcessId(WindowFromPoint(new Point(mouse.X, mouse.Y)), out uint clickedPid);
                if (clickedPid != Environment.ProcessId) _dispatcher.BeginInvoke(_dismiss);
            }
            else if (message == 0x0202)
            {
                var window = GetForegroundWindow();
                GetWindowThreadProcessId(window, out uint pid);
                if (IsExternal(window, pid))
                {
                    int generation = _generation;
                    _dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(40);
                        if (_disposed || generation != _generation) return;
                        var capture = await _bridge.TryCaptureSelectedAsync(window);
                        if (_disposed || generation != _generation) return;
                        _selection = capture; _selectionWindow = window; _selectedAt = Environment.TickCount64;
                    });
                }
            }
            else if (message == 0x0205)
            {
                nint target = GetForegroundWindow();
                GetWindowThreadProcessId(target, out uint pid);
                if (IsExternal(target, pid))
                {
                    var selected = target == _selectionWindow && Environment.TickCount64 - _selectedAt < 8000 ? _selection : null;
                    int x = mouse.X, y = mouse.Y;
                    _dispatcher.BeginInvoke(() => { if (!_disposed) _show(target, selected, x, y); });
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    internal static bool IsExternal(nint target, uint processId) => target != 0 && processId != 0 && processId != Environment.ProcessId;
    public void Dispose() { if (_disposed) return; _disposed = true; _generation++; _selection = null; if (_hook != 0) UnhookWindowsHookEx(_hook); _hook = 0; }
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Mouse, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
