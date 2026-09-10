using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CheckCheck.App;

internal static class TrayLifecycleSelfTest
{
    internal static async Task RunAsync(MainWindow window, string outputPath)
    {
        var checks = new List<string>();
        bool closed = false, exitRequested = false;
        window.Closed += (_, _) => closed = true;
        using (var tray = new TrayLifecycle(window, () => exitRequested = true, showNotice: false))
        {
            Check(tray.IsVisible, "tray icon is available before hiding");
            nint handle = new WindowInteropHelper(window).Handle;
            var background = window.BeginBackgroundLifecycleCheck();
            window.Close();
            Check(!window.IsVisible && !closed && IsWindow(handle), "X hides window and preserves its native handle");
            Check(!background.IsCancellationRequested, "X preserves background model download and review");
            window.EndBackgroundLifecycleCheck();
            tray.Restore();
            Check(window.IsVisible && !closed, "tray restores the same running window");
            window.WindowState = WindowState.Minimized;
            window.Close();
            tray.Restore();
            Check(window.IsVisible && window.WindowState == WindowState.Normal, "restore also unmiminizes a hidden window");
            tray.RequestExit();
            Check(exitRequested, "explicit exit invokes application shutdown");
            window.Close();
            Check(closed, "explicit exit permits window close and disposal");
        }

        var sessionWindow = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        bool sessionClosed = false;
        sessionWindow.Closed += (_, _) => sessionClosed = true;
        using (var sessionTray = new TrayLifecycle(sessionWindow, () => { }, showNotice: false))
        {
            sessionWindow.Show();
            sessionTray.AllowExit();
            sessionWindow.Close();
            Check(sessionClosed, "Windows session end allows closing without hiding");
        }

        string instanceName = @"Local\CheckCheck.LifecycleTest." + Guid.NewGuid().ToString("N");
        int activations = 0;
        using (var primary = new SingleInstanceCoordinator(window.Dispatcher, () => activations++, instanceName))
        {
            Check(primary.IsPrimary, "first launch owns single instance");
            using var secondary = new SingleInstanceCoordinator(window.Dispatcher, () => { }, instanceName);
            Check(!secondary.IsPrimary, "second launch cannot create a duplicate instance");
            secondary.RequestActivation();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (activations == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
            Check(activations == 1, "second launch signals the existing dispatcher to restore");
        }
        using (var restarted = new SingleInstanceCoordinator(window.Dispatcher, () => { }, instanceName))
            Check(restarted.IsPrimary, "exit releases singleton for the next launch");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, "PASS: " + checks.Count + " lifecycle checks\n" + string.Join("\n", checks));
        return;

        void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException("Lifecycle: " + name);
            checks.Add(name);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);
}
