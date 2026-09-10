using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CheckCheck.App.Native;
using CheckCheck.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace CheckCheck.App;

/// <summary>Small editor and temporary near-pointer review share the same local engine and decisions.</summary>
internal sealed class QuickReviewWindow : Window
{
    private readonly TextBox _source;
    private readonly TextBlock _status;
    private readonly StackPanel _suggestions;
    private readonly Button _review, _copy, _apply;
    private readonly ComboBox _mode;
    private readonly Func<string, ReviewMode, IProgress<EngineProgress>, CancellationToken, Task<ReviewResult>> _check;
    private readonly Func<CaptureSnapshot, string, Task<OperationResult>> _write;
    private readonly Action<string, CaptureSnapshot?> _expand;
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(45) };
    private CaptureSnapshot? _snapshot;
    private ReviewSession? _session;
    private CancellationTokenSource? _work;
    private bool _closed;

    internal QuickReviewWindow(string text, CaptureSnapshot? snapshot, bool temporary,
        Func<string, ReviewMode, IProgress<EngineProgress>, CancellationToken, Task<ReviewResult>> check,
        Func<CaptureSnapshot, string, Task<OperationResult>> write, Action<string, CaptureSnapshot?> expand, ReviewMode mode = ReviewMode.Minimal)
    {
        _snapshot = snapshot; _check = check; _write = write; _expand = expand;
        Title = temporary ? "체크체크 · 빠른 교정" : "체크체크 · 작은 창";
        Width = 420; Height = temporary ? 390 : 540; MinWidth = 355; MinHeight = 330;
        Topmost = true; ShowInTaskbar = !temporary; ShowActivated = !temporary;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F7F8F4"); Foreground = Brush("#263D33"); FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic");
        var grid = new Grid { Margin = new Thickness(16) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new RowDefinition { Height = h });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var full = MakeButton("크게 보기 ↗", (_, _) => { _expand(_source!.Text, _snapshot); Close(); });
        DockPanel.SetDock(full, Dock.Right); header.Children.Add(full);
        header.Children.Add(new TextBlock { Text = temporary ? "✓✓ 선택한 부분만 빠르게" : "✓✓ 체크체크", FontWeight = FontWeights.SemiBold, FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(header);
        _source = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 3000, FontSize = 13, Padding = new Thickness(9), Height = temporary ? 68 : 105, Background = Brushes.White };
        _source.TextChanged += (_, _) => { _work?.Cancel(); _session = null; _suggestions?.Children.Clear(); if (_snapshot?.Text != _source.Text) _snapshot = null; UpdateActions(); };
        System.Windows.Automation.AutomationProperties.SetName(_source, "작은 창 원문");
        Grid.SetRow(_source, 1); grid.Children.Add(_source);
        var toolbar = new DockPanel { Margin = new Thickness(0, 10, 0, 8) };
        _review = MakeButton("검사하기", async (_, _) => await ReviewAsync()); DockPanel.SetDock(_review, Dock.Right); toolbar.Children.Add(_review);
        _mode = new ComboBox { Width = 145, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ItemsSource = new[] { "최소 교정", "자연스럽게", "업무용 말투" }, SelectedIndex = (int)mode };
        _mode.SelectionChanged += (_, _) => { _session = null; _suggestions?.Children.Clear(); UpdateActions(); };
        toolbar.Children.Add(_mode);
        Grid.SetRow(toolbar, 2); grid.Children.Add(toolbar);
        _suggestions = new StackPanel();
        var scroll = new ScrollViewer { Content = _suggestions, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3); grid.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 7) };
        _copy = MakeButton("선택한 수정문 복사", (_, _) => { try { if (_session is not null) { System.Windows.Clipboard.SetText(_session.BuildAccepted()); _status!.Text = "복사했어요. 원래 입력칸에 붙여넣으세요."; } } catch { _status!.Text = "클립보드를 사용할 수 없어요. 다시 시도해 주세요."; } });
        _apply = MakeButton("반영", async (_, _) => await ApplyAsync());
        actions.Children.Add(_copy); actions.Children.Add(_apply); Grid.SetRow(actions, 4); grid.Children.Add(actions);
        _status = new TextBlock { Text = "무료 로컬 AI · 수정할 부분만 확인하고 선택하세요.", FontSize = 11, Foreground = Brush("#72836F"), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_status, 5); grid.Children.Add(_status); Content = grid;
        UpdateActions();
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await ReviewAsync(); } };
        Closed += (_, _) => { _closed = true; _expiry.Stop(); _work?.Cancel(); };
        if (temporary)
        {
            _expiry.Tick += (_, _) => { if (!IsMouseOver && !IsActive) Close(); };
            _expiry.Start();
        }
        if (temporary || snapshot is not null) Loaded += async (_, _) => await ReviewAsync();
    }

    internal void PlaceNear(int x, int y)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var transform = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var pointer = transform.Transform(new System.Windows.Point(x + 240, y + 12));
        var start = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var end = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        Left = Math.Max(start.X, Math.Min(pointer.X, end.X - Width)); Top = Math.Max(start.Y, Math.Min(pointer.Y, end.Y - Height));
    }

    private async Task ReviewAsync()
    {
        if (_work is not null || _closed) return;
        if (string.IsNullOrWhiteSpace(_source.Text)) { _status.Text = "검사할 글을 입력해 주세요."; return; }
        if (_source.Text.Length > 3000) { _status.Text = "작은 창에서는 3,000자까지 검사해요. 필요한 부분만 선택해 주세요."; return; }
        using var work = new CancellationTokenSource(); _work = work;
        _review.IsEnabled = _mode.IsEnabled = false; _review.Content = "검사 중…"; _session = null; UpdateActions();
        _suggestions.Children.Clear(); _status.Text = "선택한 문장을 로컬 AI로 확인하고 있어요…";
        string text = _source.Text;
        try
        {
            var result = await _check(text, (ReviewMode)_mode.SelectedIndex, new Progress<EngineProgress>(p => { if (!_closed && !work.IsCancellationRequested) _status.Text = p.Message; }), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (_closed || _source.Text != text) return;
            _session = new ReviewSession(text, result.Suggestions);
            foreach (var suggestion in _session.Suggestions)
            {
                int index = _suggestions.Children.Count;
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = suggestion.Original.Length == 0 ? "(삽입)" : suggestion.Original, Foreground = Brush("#986852"), TextWrapping = TextWrapping.Wrap, TextDecorations = TextDecorations.Strikethrough });
                label.Children.Add(new TextBlock { Text = "→ " + suggestion.Replacement, Foreground = Brush("#24664F"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                var choice = new CheckBox { Content = label, Margin = new Thickness(0, 5, 7, 9), HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
                choice.Checked += (_, _) => { _session?.SetDecision(index, SuggestionDecision.Accepted); UpdateActions(); };
                choice.Unchecked += (_, _) => { _session?.SetDecision(index, SuggestionDecision.Skipped); UpdateActions(); };
                _suggestions.Children.Add(choice);
            }
            _status.Text = result.Suggestions.Count == 0 ? "수정 제안을 찾지 못했어요. " + result.Note : $"수정 제안 {result.Suggestions.Count}개 · 반영할 항목을 체크하세요.";
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = "원문이 바뀌어 검사를 중지했어요. 다시 검사해 주세요."; }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { _work = null; if (!_closed) { _review.IsEnabled = _mode.IsEnabled = true; _review.Content = "검사하기"; UpdateActions(); } }
    }

    private async Task ApplyAsync()
    {
        if (_session is null || _snapshot is null || _work is not null) return;
        var revised = _session.BuildAccepted();
        _apply.IsEnabled = false;
        var result = await _write(_snapshot, revised);
        if (_closed) return;
        _status.Text = result.Message;
        if (result.Success) { _snapshot = null; _source.Text = revised; }
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (_copy is null || _apply is null) return;
        _copy.IsEnabled = _work is null && _session is not null;
        _apply.IsEnabled = _work is null && _session is not null && _snapshot?.CanApply == true && _session.BuildAccepted() != _session.Original;
        _apply.ToolTip = _snapshot?.CanApply == true ? "선택한 수정 사항을 원래 입력칸에 반영" : "이 프로그램에서는 복사해서 붙여넣어 주세요.";
    }

    internal async Task RunSmokeAsync(string path)
    {
        await ReviewAsync();
        if (_session is null || _session.Suggestions.Count == 0) throw new InvalidOperationException("Quick UI: expected corrections.");
        var original = _session.Original;
        ((CheckBox)_suggestions.Children[0]).IsChecked = true;
        if (_session.BuildAccepted() == original || !_copy.IsEnabled) throw new InvalidOperationException("Quick UI: selection must update copy output.");
        if (_apply.IsEnabled) throw new InvalidOperationException("Quick UI: copy-only source cannot enable native apply.");
        UpdateLayout();
        var surface = (FrameworkElement)Content;
        foreach (var control in new FrameworkElement[] { _source, _status, _copy, _apply })
        {
            var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            if (bounds.Bottom > surface.ActualHeight + 1 || bounds.Right > surface.ActualWidth + 1) throw new InvalidOperationException("Quick UI: clipped controls.");
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        using (var stream = System.IO.File.Create(path))
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }
        _source.Text += " 새 원문";
        if (_session is not null || _copy.IsEnabled) throw new InvalidOperationException("Quick UI: stale result survived source edit.");
    }

    private static Button MakeButton(string text, RoutedEventHandler click) { var button = new Button { Content = text, Padding = new Thickness(10, 7, 10, 7), FontSize = 11, Margin = new Thickness(4, 0, 0, 0) }; button.Click += click; return button; }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
