using System.IO;
using System.Windows;
using CheckCheck.App.Native;
using Application = System.Windows.Application;

namespace CheckCheck.App;

public partial class App : Application
{
    private TrayLifecycle? _tray;
    private SingleInstanceCoordinator? _instance;
    internal static bool IsTestRun { get; private set; }
    internal static bool IsLiveBenchmark { get; private set; }
    internal void ExitForUpdate() { _tray?.AllowExit(); Shutdown(); }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsTestRun = e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal);
        IsLiveBenchmark = e.Args.Length >= 3 && e.Args[0] is "--benchmark-file" or "--benchmark-quick-file";
        if (e.Args.Length > 0 && e.Args[0] == "--native-fixture") { NativeSelfTest.RunFixture(); Shutdown(); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--native-self-test")
        {
            try { await NativeSelfTest.RunAsync(e.Args[1]); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }
        if (!IsTestRun)
        {
            _instance = new SingleInstanceCoordinator(Dispatcher, () => _tray?.Restore());
            if (!_instance.IsPrimary) { _instance.RequestActivation(); Shutdown(); return; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SessionEnding += (_, _) => _tray?.AllowExit();
        }
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            System.Windows.MessageBox.Show("작업을 완료하지 못했어요. 원문을 확인한 뒤 다시 시도해 주세요.", "체크체크", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        var main = new MainWindow(); MainWindow = main;
        if (IsLiveBenchmark)
        {
            main.ShowInTaskbar = false; main.Left = -20000; main.Top = -20000;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Loaded += async (_, _) =>
            {
                try
                {
                    if (e.Args[0] == "--benchmark-quick-file") await main.RunQuickArticleBenchmarkAsync(e.Args[1], e.Args[2]);
                    else await main.RunArticleBenchmarkAsync(e.Args[1], e.Args[2]);
                    Shutdown(0);
                }
                catch (Exception ex) { File.WriteAllText(e.Args[2] + ".error", ex.ToString()); Shutdown(1); }
            };
        }
        if (!IsTestRun) _tray = new TrayLifecycle(main, () => Shutdown());
        if (e.Args.Length >= 2 && e.Args[0] == "--lifecycle-self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            main.ShowInTaskbar = false;
            main.Left = -20000; main.Top = -20000;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Loaded += async (_, _) =>
            {
                try { await TrayLifecycleSelfTest.RunAsync(main, e.Args[1]); Shutdown(0); }
                catch (Exception ex) { File.WriteAllText(e.Args[1], "FAIL: " + ex); Shutdown(1); }
            };
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--smoke-test")
        {
            if (e.Args.Length >= 4 && double.TryParse(e.Args[2], out var width) && double.TryParse(e.Args[3], out var height)) { main.Width = width; main.Height = height; }
            main.ShowInTaskbar = false;
            main.Left = -20000; main.Top = -20000;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Loaded += async (_, _) =>
            {
                try { await main.RunUiSmokeAsync(e.Args[1]); await main.RunQuickSmokeAsync(e.Args[1]); File.WriteAllText(e.Args[1] + ".result", "PASS: local default, preference migration, review, acceptance, undo, stale results, hotkey conflict/change, compact/popup render"); Shutdown(0); }
                catch (Exception ex) { File.WriteAllText(e.Args[1] + ".result", "FAIL: " + ex); Shutdown(1); }
            };
        }
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
