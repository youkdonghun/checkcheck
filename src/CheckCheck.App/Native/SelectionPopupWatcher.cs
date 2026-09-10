using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CheckCheck.App.Native;

/// <summary>Displays a launcher on any ordinary external right click, independent of text-provider success.</summary>
internal sealed class SelectionPopupWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<nint, CaptureSnapshot?, int, int> _show;
    private readonly Action _dismiss;
    private readonly HookProc _callback;
    private nint _hook, _rightTarget;
    private bool _disposed;

    internal SelectionPopupWatcher(NativeTextBridge bridge, Dispatcher dispatcher, Action<nint, CaptureSnapshot?, int, int> show, Action dismiss)
    {
        _dispatcher = dispatcher; _show = show; _dismiss = dismiss;
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
                GetWindowThreadProcessId(WindowFromPoint(new Point(mouse.X, mouse.Y)), out uint clickedPid);
                if (clickedPid != Environment.ProcessId) _dispatcher.BeginInvoke(_dismiss);
            }
            else if (message == 0x0204)
            {
                // Remember the window under the pointer before its native context menu opens.
                // Do not inspect text on normal clicks: UIA runs only after an explicit command.
                _rightTarget = GetAncestor(WindowFromPoint(new Point(mouse.X, mouse.Y)), 2);
            }
            else if (message == 0x0205)
            {
                nint target = _rightTarget;
                _rightTarget = 0;
                GetWindowThreadProcessId(target, out uint pid);
                if (IsExternal(target, pid))
                {
                    int x = mouse.X, y = mouse.Y;
                    _dispatcher.BeginInvoke(() => { if (!_disposed) _show(target, null, x, y); });
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    internal static bool IsExternal(nint target, uint processId) => target != 0 && processId != 0 && processId != Environment.ProcessId;
    public void Dispose() { if (_disposed) return; _disposed = true; _rightTarget = 0; if (_hook != 0) UnhookWindowsHookEx(_hook); _hook = 0; }
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Mouse, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
