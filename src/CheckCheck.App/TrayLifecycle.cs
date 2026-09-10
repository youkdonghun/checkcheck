using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace CheckCheck.App;

internal sealed class TrayLifecycle : IDisposable
{
    private readonly Window _window;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _image;
    private readonly bool _showNotice;
    private bool _allowExit, _noticeShown, _disposed;

    internal TrayLifecycle(Window window, Action exit, bool showNotice = true)
    {
        _window = window;
        _exit = exit;
        _showNotice = showNotice;
        _image = LoadIcon();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("체크체크 열기", null, (_, _) => Restore());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("체크체크 종료", null, (_, _) => RequestExit());
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = "체크체크 · 두 번 클릭해서 열기",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Restore();
        _icon.BalloonTipClicked += (_, _) => Restore();
        _window.Closing += WindowClosing;
    }

    internal bool IsVisible => !_disposed && _icon.Visible;

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch { /* System icon is available when the executable icon cannot be read. */ }
        return (Icon)SystemIcons.Application.Clone();
    }

    private void WindowClosing(object? sender, CancelEventArgs args)
    {
        if (_allowExit || _disposed) return;
        args.Cancel = true;
        _window.Hide();
        if (_showNotice && !_noticeShown)
        {
            _noticeShown = true;
            _icon.ShowBalloonTip(4000, "체크체크가 백그라운드에서 실행 중이에요",
                "설정한 단축키로 글을 가져오거나, 작업 표시줄의 체크체크 아이콘을 두 번 눌러 다시 여세요. 종료는 아이콘의 오른쪽 메뉴에서 할 수 있어요.", Forms.ToolTipIcon.Info);
        }
    }

    internal void Restore()
    {
        if (_disposed || _allowExit) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.BeginInvoke(Restore); return; }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        // A second launch is permitted to bring an existing hidden window forward.
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd != 0) SetForegroundWindow(hwnd);
    }

    internal void AllowExit() => _allowExit = true;

    internal void RequestExit()
    {
        if (_disposed || _allowExit) return;
        _allowExit = true;
        _exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Closing -= WindowClosing;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
