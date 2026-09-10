using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CheckCheck.App;

internal sealed class QuickLauncherWindow : Window
{
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(8) };

    internal QuickLauncherWindow(bool hasSelection, Action review)
    {
        Title = "체크체크 빠른 검사"; Width = 218; Height = 42;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F7F8F4"));
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var go = new Button { Content = hasSelection ? "✓✓ 선택한 글 검사" : "✓✓ 체크체크로 검사", FontSize = 12, Padding = new Thickness(6), ToolTip = "선택한 글을 가져와 검사합니다. 글을 읽지 못해도 붙여넣기 창을 열어요." };
        go.Click += (_, _) => { Close(); review(); }; grid.Children.Add(go);
        var dismiss = new Button { Content = "×", FontSize = 16, Padding = new Thickness(2), ToolTip = "닫기" };
        dismiss.Click += (_, _) => Close(); Grid.SetColumn(dismiss, 1); grid.Children.Add(dismiss);
        Content = new Border { BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFD4C5")), BorderThickness = new Thickness(1), Child = grid };
        _expiry.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        Closed += (_, _) => _expiry.Stop(); _expiry.Start();
    }

    internal void PlaceNear(int x, int y)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var transform = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var pointer = transform.Transform(new System.Windows.Point(x + 12, y));
        var start = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var end = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        Left = Math.Max(start.X, Math.Min(pointer.X, end.X - Width));
        Top = pointer.Y - Height - 8 >= start.Y ? pointer.Y - Height - 8 : Math.Min(pointer.Y + 30, end.Y - Height);
    }
}
