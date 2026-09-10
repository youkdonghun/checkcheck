using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CheckCheck.App.Native;

/// <summary>Observes drag then right-click without suppressing or replacing any mouse event.</summary>
internal sealed class SelectionPopupWatcher : IDisposable
{
    private readonly NativeTextBridge _bridge;
    private readonly Dispatcher _dispatcher;
    private readonly Action<CaptureSnapshot, int, int> _show;
    private readonly HookProc _callback;
    private nint _hook, _dragWindow;
    private int _startX, _startY, _generation;
    private bool _dragging, _disposed;
    private long _selectedAt;
    private CaptureSnapshot? _selection;

    internal SelectionPopupWatcher(NativeTextBridge bridge, Dispatcher dispatcher, Action<CaptureSnapshot, int, int> show)
    {
        _bridge = bridge; _dispatcher = dispatcher; _show = show;
        _callback = Observe;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new InvalidOperationException("우클릭 감지를 시작하지 못했어요. 단축키로 작은 창을 이용해 주세요.");
    }

    private nint Observe(int code, nint message, nint data)
    {
        if (code >= 0 && !_disposed)
        {
            var mouse = Marshal.PtrToStructure<MouseData>(data);
            if ((mouse.Flags & 1) == 0)
            {
                if (message == 0x0201) // left down
                {
                    _selection = null; _generation++;
                    _dragging = true; _startX = mouse.X; _startY = mouse.Y;
                    _dragWindow = GetForegroundWindow();
                }
                else if (message == 0x0202 && _dragging) // left up
                {
                    _dragging = false;
                    var window = GetForegroundWindow();
                    GetWindowThreadProcessId(window, out uint pid);
                    if (pid != Environment.ProcessId && IsDrag(_startX, _startY, mouse.X, mouse.Y))
                    {
                        _dragWindow = window;
                        int generation = _generation;
                        _dispatcher.BeginInvoke(async () =>
                        {
                            await Task.Delay(40);
                            if (_disposed || generation != _generation) return;
                            var capture = await _bridge.TryCaptureSelectedAsync(window);
                            if (_disposed || generation != _generation) return;
                            _selection = capture; _selectedAt = Environment.TickCount64;
                        });
                    }
                }
                else if (message == 0x0205 && _selection is { } selected && GetForegroundWindow() == _dragWindow && Environment.TickCount64 - _selectedAt < 8000)
                {
                    int x = mouse.X, y = mouse.Y;
                    _selection = null;
                    _dispatcher.BeginInvoke(() => { if (!_disposed) _show(selected, x, y); });
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    internal static bool IsDrag(int x1, int y1, int x2, int y2) => Math.Abs(x2 - x1) >= 6 || Math.Abs(y2 - y1) >= 6;
    public void Dispose() { if (_disposed) return; _disposed = true; _generation++; _selection = null; if (_hook != 0) UnhookWindowsHookEx(_hook); _hook = 0; }
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Mouse, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
