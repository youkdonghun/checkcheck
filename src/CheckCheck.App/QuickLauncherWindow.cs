using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CheckCheck.Core;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace CheckCheck.App;

/// <summary>Companion to the native menu. Text is captured only after an explicit action.</summary>
internal sealed class QuickLauncherWindow : Window
{
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly List<Button> _modeButtons = new();
    private readonly Button _pasteButton;

    internal QuickLauncherWindow(bool hasSelection, Action review)
        : this(hasSelection, _ => review(), review) { }

    internal QuickLauncherWindow(bool hasSelection, Action<ReviewMode> review, Action paste)
    {
        Title = "체크체크 빠른 검사"; Width = 314; Height = 142;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic"); Background = Brush("#F6F8F5");
        var body = new StackPanel { Margin = new Thickness(12, 8, 12, 9) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var dismiss = new Button { Content = "×", FontSize = 17, Padding = new Thickness(7, 0, 7, 0), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "닫기" };
        dismiss.Click += (_, _) => Close(); DockPanel.SetDock(dismiss, Dock.Right); header.Children.Add(dismiss);
        header.Children.Add(new TextBlock { Text = "✓✓ 체크체크", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brush("#24664F"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(header);
        var choices = new Grid();
        string[] labels = { "최소 교정", "자연스럽게", "업무용" };
        for (int i = 0; i < labels.Length; i++)
        {
            choices.ColumnDefinitions.Add(new ColumnDefinition());
            var mode = (ReviewMode)i;
            var go = new Button { Content = labels[i], FontSize = 13, Padding = new Thickness(7, 9, 7, 9), Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0), ToolTip = "선택한 글을 가져와 " + labels[i] + " 방식으로 검사합니다." };
            if (i == 0) { go.Background = Brush("#24664F"); go.Foreground = System.Windows.Media.Brushes.White; go.BorderBrush = Brush("#24664F"); }
            go.Click += (_, _) => { Close(); review(mode); }; Grid.SetColumn(go, i); choices.Children.Add(go); _modeButtons.Add(go);
        }
        body.Children.Add(choices);
        var bottom = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        var clipboard = _pasteButton = new Button { Content = "복사한 글 검사", FontSize = 12, Padding = new Thickness(7, 3, 7, 3), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brush("#24664F") };
        clipboard.Click += (_, _) => { Close(); paste(); }; DockPanel.SetDock(clipboard, Dock.Right); bottom.Children.Add(clipboard);
        bottom.Children.Add(new TextBlock { Text = hasSelection ? "선택한 글을 검사해요" : "글을 선택한 뒤 누르세요", FontSize = 11, Foreground = Brush("#718078"), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(bottom);
        Content = new Border { Background = Background, BorderBrush = Brush("#BFD4C5"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = body };
        _expiry.Tick += (_, _) => { if (!IsMouseOver && !IsActive) Close(); };
        Closed += (_, _) => _expiry.Stop(); _expiry.Start();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
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
        Top = pointer.Y - Height - 8 >= start.Y ? pointer.Y - Height - 8 : Math.Max(start.Y, Math.Min(pointer.Y + 30, end.Y - Height));
    }

    internal static void RunSmoke(string imagePath)
    {
        for (int action = 0; action < 4; action++)
        {
            ReviewMode? selected = null; bool pasted = false, closed = false;
            var launcher = new QuickLauncherWindow(action % 2 == 0, mode => selected = mode, () => pasted = true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
            };
            launcher.Closed += (_, _) => closed = true;
            launcher.Show();
            try
            {
                launcher.UpdateLayout();
                var surface = (FrameworkElement)launcher.Content;
                foreach (var button in launcher._modeButtons.Append(launcher._pasteButton))
                {
                    var bounds = button.TransformToAncestor(surface).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > surface.ActualWidth + 1 || bounds.Bottom > surface.ActualHeight + 1)
                        throw new InvalidOperationException("Quick launcher: clipped action.");
                }
                if (action == 0)
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual();
                    using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                    bitmap.Render(drawing);
                    using var stream = System.IO.File.Create(imagePath);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(stream);
                }
                (action < 3 ? launcher._modeButtons[action] : launcher._pasteButton).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!closed || (action < 3 ? selected != (ReviewMode)action || pasted : !pasted || selected is not null))
                    throw new InvalidOperationException("Quick launcher: selected action was not dispatched exactly.");
            }
            finally { if (!closed) launcher.Close(); }
        }
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
