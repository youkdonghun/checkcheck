using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CheckCheck.App.Native;
using CheckCheck.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace CheckCheck.App;

/// <summary>A persistent, resizable review beside the original app. Only accepted patches leave this window.</summary>
internal sealed class QuickReviewWindow : Window
{
    private readonly TextBox _source, _preview;
    private readonly TextBlock _status, _summary, _sourceLabel;
    private readonly Expander _sourceSection;
    private readonly StackPanel _suggestions;
    private readonly ScrollViewer _suggestionScroll;
    private readonly Button _review, _copy, _apply, _acceptAll, _clear, _undo, _suggestionTab, _previewTab;
    private readonly ComboBox _mode;
    private readonly ProgressBar _progress;
    private readonly List<CorrectionCard> _cards = new();
    private readonly Stack<Dictionary<CorrectionKey, SuggestionDecision>> _decisionHistory = new();
    private readonly Func<string, ReviewMode, IProgress<EngineProgress>, CancellationToken, Task<ReviewResult>> _check;
    private readonly Func<CaptureSnapshot, string, Task<OperationResult>> _write;
    private readonly Action<string, CaptureSnapshot?> _expand;
    private CaptureSnapshot? _snapshot;
    private ReviewSession? _session;
    private CancellationTokenSource? _work;
    private bool _closed, _showPreview, _complete;

    internal bool IsReviewing => _work is not null;
    internal double LastReviewSeconds { get; private set; }
    internal double? FirstResultSeconds { get; private set; }
    internal Task RunReviewForBenchmarkAsync() => ReviewAsync();
    internal void PasteForBenchmark() => PasteSource();

    internal QuickReviewWindow(string text, CaptureSnapshot? snapshot, bool temporary,
        Func<string, ReviewMode, IProgress<EngineProgress>, CancellationToken, Task<ReviewResult>> check,
        Func<CaptureSnapshot, string, Task<OperationResult>> write, Action<string, CaptureSnapshot?> expand, ReviewMode mode = ReviewMode.Minimal)
    {
        _snapshot = snapshot; _check = check; _write = write; _expand = expand;
        Title = "체크체크 · 선택한 글 교정";
        Width = 680; Height = 740; MinWidth = 520; MinHeight = 520;
        Topmost = true; ShowInTaskbar = !temporary; ShowActivated = !temporary;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("#F6F8F5"); Foreground = Brush("#263D33"); FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic"); FontSize = 15;
        var grid = new Grid { Margin = new Thickness(20, 16, 20, 14), Background = Background };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = h });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal };
        var pin = MakeButton("고정 켜짐", (_, _) => { Topmost = !Topmost; }, small: true);
        pin.Click += (_, _) => pin.Content = Topmost ? "고정 켜짐" : "고정 꺼짐";
        pin.ToolTip = "다른 앱을 사용해도 검토 창을 위에 표시합니다.";
        headerActions.Children.Add(pin);
        headerActions.Children.Add(MakeButton("전체 창 ↗", (_, _) => { _expand(_source!.Text, _snapshot); Close(); }, small: true));
        DockPanel.SetDock(headerActions, Dock.Right); header.Children.Add(headerActions);
        var branding = new StackPanel();
        branding.Children.Add(new TextBlock { Text = "✓✓ 체크체크", FontWeight = FontWeights.Bold, FontSize = 22 });
        branding.Children.Add(new TextBlock { Text = "문맥을 보고, 필요한 수정만 채택하세요", FontSize = 12, Foreground = Brush("#708076"), Margin = new Thickness(0, 3, 0, 0) });
        header.Children.Add(branding); grid.Children.Add(header);

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _review = MakeButton("검사하기", async (_, _) => { if (_work is not null) _work.Cancel(); else await ReviewAsync(); }, primary: true);
        DockPanel.SetDock(_review, Dock.Right); toolbar.Children.Add(_review);
        var paste = MakeButton("붙여넣기", (_, _) => PasteSource());
        DockPanel.SetDock(paste, Dock.Right); toolbar.Children.Add(paste);
        _mode = new ComboBox { Width = 154, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 14, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center,
            ItemsSource = new[] { "최소 교정", "자연스럽게", "업무용 말투" }, SelectedIndex = (int)mode };
        System.Windows.Automation.AutomationProperties.SetName(_mode, "교정 방식");
        toolbar.Children.Add(_mode); Grid.SetRow(toolbar, 1); grid.Children.Add(toolbar);

        _source = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 10000, FontSize = 15, Padding = new Thickness(12), Height = 116, Background = Brushes.White, Margin = new Thickness(0, 8, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(_source, "교정할 원문");
        _sourceLabel = new TextBlock { Text = SourceLabel(text), FontSize = 13, Foreground = Brush("#61756A") };
        _sourceSection = new Expander { Header = _sourceLabel, Content = _source, IsExpanded = string.IsNullOrWhiteSpace(text), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(0, 4, 0, 4) };
        Grid.SetRow(_sourceSection, 2); grid.Children.Add(_sourceSection);

        var resultHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        var views = new DockPanel();
        _summary = new TextBlock { Text = "검사 대기", FontSize = 12, Foreground = Brush("#708076"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(_summary, Dock.Right); views.Children.Add(_summary);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        _suggestionTab = MakeButton("수정 제안", (_, _) => ShowView(false), small: true);
        _suggestionTab.Margin = new Thickness(0, 0, 5, 0);
        _previewTab = MakeButton("전체 결과", (_, _) => ShowView(true), small: true);
        tabs.Children.Add(_suggestionTab); tabs.Children.Add(_previewTab); views.Children.Add(tabs); resultHeader.Children.Add(views);
        var decisions = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
        _acceptAll = MakeButton("모두 채택", (_, _) => ChangeDecisions(() => _session!.AcceptAll()), small: true);
        _acceptAll.Margin = new Thickness(0, 0, 5, 0);
        _clear = MakeButton("모두 해제", (_, _) => ChangeDecisions(() => { for (int i = 0; i < _session!.Suggestions.Count; i++) _session.SetDecision(i, SuggestionDecision.Pending); }), small: true);
        _undo = MakeButton("되돌리기", (_, _) => UndoDecisions(), small: true);
        decisions.Children.Add(_acceptAll); decisions.Children.Add(_clear); decisions.Children.Add(_undo); resultHeader.Children.Add(decisions);
        Grid.SetRow(resultHeader, 3); grid.Children.Add(resultHeader);

        var resultArea = new Grid();
        _suggestions = new StackPanel();
        _suggestionScroll = new ScrollViewer { Content = _suggestions, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) };
        _preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 15, Padding = new Thickness(16), Background = Brushes.White,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetName(_preview, "채택한 수정을 반영한 전체 결과");
        resultArea.Children.Add(_suggestionScroll); resultArea.Children.Add(_preview); Grid.SetRow(resultArea, 4); grid.Children.Add(resultArea);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 9) };
        var actions = new DockPanel();
        _apply = MakeButton("원래 앱에 반영", async (_, _) => await ApplyAsync());
        DockPanel.SetDock(_apply, Dock.Right); actions.Children.Add(_apply);
        _copy = MakeButton("수정문 복사", (_, _) => CopyAccepted(), primary: true);
        DockPanel.SetDock(_copy, Dock.Right); actions.Children.Add(_copy);
        actions.Children.Add(new TextBlock { Text = "채택한 수정만 반영", FontSize = 12, Foreground = Brush("#708076"), VerticalAlignment = VerticalAlignment.Center });
        footer.Children.Add(actions);
        _progress = new ProgressBar { Height = 3, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed, Foreground = Brush("#2E795B") };
        footer.Children.Add(_progress); Grid.SetRow(footer, 5); grid.Children.Add(footer);
        _status = new TextBlock { Text = "글을 붙여넣거나 선택한 글을 가져와 검사하세요. Ctrl+Enter로 검사", FontSize = 12, Foreground = Brush("#61756A"), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_status, 6); grid.Children.Add(_status); Content = grid;

        _source.TextChanged += (_, _) => InvalidateResult();
        _mode.SelectionChanged += (_, _) => InvalidateResult();
        ShowEmpty("선택한 글을 이곳에서 검토하세요", "수정 전과 후를 문장 안에서 비교하고, 필요한 항목만 채택할 수 있어요.");
        ShowView(false); UpdateActions();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; if (_work is not null) _work.Cancel(); else Close(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await ReviewAsync(); }
        };
        Closed += (_, _) => { _closed = true; _work?.Cancel(); };
        if (!string.IsNullOrWhiteSpace(text) && (temporary || snapshot is not null)) Loaded += async (_, _) => await ReviewAsync();
    }

    internal void SetNotice(string message) => _status.Text = message;

    internal async Task LoadCaptureAsync(CaptureSnapshot capture)
    {
        if (_closed || _source.Text.Length > 0 || IsReviewing) return;
        _source.Text = capture.Text;
        _snapshot = capture;
        await ReviewAsync();
    }

    internal void PlaceNear(int x, int y)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var transform = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var pointer = transform.Transform(new System.Windows.Point(x + 18, y + 12));
        var start = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var end = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        // All values below are DIPs, including monitors with different scaling.
        MinWidth = Math.Min(520, Math.Max(320, end.X - start.X)); MinHeight = Math.Min(520, Math.Max(320, end.Y - start.Y));
        Width = Math.Min(Width, end.X - start.X); Height = Math.Min(Height, end.Y - start.Y);
        Left = Math.Max(start.X, Math.Min(pointer.X, end.X - Width)); Top = Math.Max(start.Y, Math.Min(pointer.Y, end.Y - Height));
    }

    private void PasteSource()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) { _status.Text = "복사한 글이 없어요. 다른 앱에서 글을 복사해 주세요."; return; }
            var text = System.Windows.Clipboard.GetText();
            if (text.Length > 10000) { _status.Text = "한 번에 10,000자까지 검사할 수 있어요. 필요한 부분을 복사해 주세요."; return; }
            _source.Text = text; _sourceSection.IsExpanded = true; _status.Text = "붙여넣었어요. 검사하기 또는 Ctrl+Enter를 누르세요.";
        }
        catch { _status.Text = "클립보드를 사용할 수 없어요. 다시 시도해 주세요."; }
    }

    private void InvalidateResult()
    {
        _work?.Cancel(); _session = null; _complete = false; _decisionHistory.Clear(); _cards.Clear(); _preview.Clear();
        _sourceLabel.Text = SourceLabel(_source.Text);
        if (_snapshot?.Text != _source.Text) _snapshot = null;
        ShowEmpty("검사할 준비가 됐어요", "검사하기를 누르면 수정할 부분을 문장별로 표시해요.");
        _status.Text = "원문이나 교정 방식이 바뀌었어요. 다시 검사해 주세요.";
        UpdateActions();
    }

    private async Task ReviewAsync()
    {
        if (_work is not null || _closed) return;
        if (string.IsNullOrWhiteSpace(_source.Text)) { _sourceSection.IsExpanded = true; _source.Focus(); _status.Text = "검사할 글을 입력해 주세요."; return; }
        if (_source.Text.Length > 10000) { _status.Text = "한 번에 10,000자까지 검사할 수 있어요. 필요한 부분만 선택해 주세요."; return; }
        using var work = new CancellationTokenSource(); _work = work;
        _mode.IsEnabled = false; _review.Content = "검사 중지"; _sourceSection.IsExpanded = false;
        _session = null; _complete = false; FirstResultSeconds = null; _cards.Clear(); _decisionHistory.Clear(); _preview.Clear(); UpdateActions(); ShowView(false);
        ShowEmpty("글을 확인하고 있어요", "이 창은 자동으로 닫히지 않아요. 검사 중지 버튼이나 Esc로 멈출 수 있어요.");
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string phase = "AI 준비 중";
        var elapsed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        elapsed.Tick += (_, _) => { if (!_closed && !work.IsCancellationRequested) _status.Text = $"{phase} · {watch.Elapsed.TotalSeconds:F1}초"; };
        elapsed.Start();
        string text = _source.Text;
        try
        {
            var result = await _check(text, (ReviewMode)_mode.SelectedIndex, new Progress<EngineProgress>(p =>
            {
                if (_closed || work.IsCancellationRequested || _work != work) return;
                phase = p.Message;
                _progress.IsIndeterminate = p.Fraction is null;
                if (p.Fraction is double fraction) _progress.Value = Math.Clamp(fraction * 100, 0, 100);
                if (p.PartialResult is { } partial && partial.Original == text && _source.Text == text)
                {
                    FirstResultSeconds ??= watch.Elapsed.TotalSeconds;
                    MergeResult(partial);
                }
            }), work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (_closed || _source.Text != text) return;
            FirstResultSeconds ??= watch.Elapsed.TotalSeconds;
            _complete = true;
            MergeResult(result);
            _status.Text = result.Suggestions.Count == 0
                ? $"{watch.Elapsed.TotalSeconds:F1}초 · 수정 제안을 찾지 못했어요. {result.Note}"
                : $"{watch.Elapsed.TotalSeconds:F1}초 · {result.Suggestions.Count}개 제안 · 수정 전후를 확인한 뒤 채택해 주세요. {result.Note}";
        }
        catch (OperationCanceledException) { if (!_closed) { _complete = false; if (_session is null) ShowEmpty("검사를 중지했어요", "글을 고치거나 검사할 부분을 줄인 뒤 다시 시작할 수 있어요."); _status.Text = $"{watch.Elapsed.TotalSeconds:F1}초에 중지 · 완료한 구간의 제안만 표시합니다. 전체 검사를 마쳐야 복사할 수 있어요."; } }
        catch (Exception ex) { if (!_closed) { _complete = false; if (_session is null) ShowEmpty("검사를 마치지 못했어요", ex.Message); _status.Text = ex.Message; } }
        finally
        {
            elapsed.Stop(); LastReviewSeconds = watch.Elapsed.TotalSeconds; _work = null;
            if (!_closed) { _progress.Visibility = Visibility.Collapsed; _review.IsEnabled = _mode.IsEnabled = true; _review.Content = "다시 검사"; UpdateActions(); }
        }
    }

    private void MergeResult(ReviewResult result)
    {
        var decisions = _session?.Suggestions.ToDictionary(KeyFor, s => s.Decision);
        _session = new ReviewSession(result.Original, result.Suggestions);
        for (int i = 0; i < _session.Suggestions.Count; i++)
            if (decisions?.TryGetValue(KeyFor(_session.Suggestions[i]), out var decision) == true) _session.SetDecision(i, decision);
        RenderSuggestions();
    }

    private void RenderSuggestions()
    {
        if (_session is null) return;
        double offset = _suggestionScroll.VerticalOffset;
        if (_cards.Count > _session.Suggestions.Count || _cards.Where((card, index) => card.Key != KeyFor(_session.Suggestions[index])).Any())
        { _suggestions.Children.Clear(); _cards.Clear(); }
        if (_session.Suggestions.Count == 0)
        {
            ShowEmpty(_complete ? "발견된 수정 제안이 없어요" : "확인한 구간에는 수정 제안이 없어요", _complete ? "전체 결과에서 글을 확인하고 복사할 수 있어요." : "나머지 글도 이어서 확인하고 있어요.");
            UpdateActions(); return;
        }
        if (_cards.Count == 0) _suggestions.Children.Clear();
        for (int index = 0; index < _session.Suggestions.Count; index++)
        {
            if (index < _cards.Count) continue;
            int position = index;
            var suggestion = _session.Suggestions[index];
            var content = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var decision = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(decision, Dock.Right); head.Children.Add(decision);
            head.Children.Add(new TextBlock { Text = $"{index + 1:00}  {suggestion.Category}", Foreground = Brush("#61756A"), FontSize = 12, FontWeight = FontWeights.SemiBold }); content.Children.Add(head);
            var (before, after) = BuildContext(suggestion);
            content.Children.Add(ComparisonLine("수정 전", before, "#A26358"));
            content.Children.Add(ComparisonLine("수정 후", after, "#24664F"));
            if (!string.IsNullOrWhiteSpace(suggestion.Reason)) content.Children.Add(new TextBlock { Text = suggestion.Reason, FontSize = 12, Foreground = Brush("#708076"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) });
            var decisions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var skip = MakeButton("넘기기", (_, _) => ChangeDecisions(() => _session!.SetDecision(position, SuggestionDecision.Skipped)), small: true);
            var accept = MakeButton("채택", (_, _) => ChangeDecisions(() => _session!.SetDecision(position, SuggestionDecision.Accepted)), primary: true, small: true);
            decisions.Children.Add(skip); decisions.Children.Add(accept); content.Children.Add(decisions);
            var shell = new Border { Child = content, Background = Brushes.White, BorderBrush = Brush("#DCE5DD"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12) };
            _cards.Add(new(KeyFor(suggestion), shell, decision, accept, skip)); _suggestions.Children.Add(shell);
        }
        UpdateActions();
        _suggestionScroll.ScrollToVerticalOffset(offset);
    }

    private (TextBlock Before, TextBlock After) BuildContext(Suggestion suggestion)
    {
        var source = _session!.Original;
        int start = suggestion.Start, end = suggestion.Start + suggestion.Length;
        int left = start, right = end;
        while (left > 0 && start - left < 65 && source[left - 1] is not ('\n' or '\r' or '.' or '!' or '?' or '。')) left--;
        while (right < source.Length && right - end < 80 && source[right] is not ('\n' or '\r'))
        {
            var c = source[right++]; if (c is '.' or '!' or '?' or '。') break;
        }
        if (left > 0 && left < source.Length && char.IsLowSurrogate(source[left]) && char.IsHighSurrogate(source[left - 1])) left--;
        if (right < source.Length && right > 0 && char.IsHighSurrogate(source[right - 1]) && char.IsLowSurrogate(source[right])) right++;
        var prefix = (left > 0 && start - left >= 65 ? "…" : "") + source[left..start];
        var suffix = source[end..right] + (right < source.Length && right - end >= 80 ? "…" : "");
        return (ContextText(prefix, suggestion.Original, suffix, false), ContextText(prefix, suggestion.Replacement, suffix, true));
    }

    private static TextBlock ContextText(string prefix, string changed, string suffix, bool after)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 25, Foreground = Brush("#43564A") };
        block.Inlines.Add(new Run(prefix));
        var markedText = changed.Length == 0 ? (after ? "(삭제)" : "(삽입 위치)")
            : changed.All(char.IsWhiteSpace) ? changed.Replace("\r\n", "↵").Replace("\n", "↵").Replace("\r", "↵").Replace("\t", "⇥").Replace(" ", "␠") : changed;
        var mark = new Run(markedText)
        {
            Background = Brush(after ? "#DDEFE2" : "#F9E4DE"), Foreground = Brush(after ? "#165439" : "#985246"), FontWeight = FontWeights.SemiBold,
            ToolTip = "␠ 공백 · ↵ 줄바꿈 · ⇥ 탭 (결과에는 원래 문자가 반영됩니다)"
        };
        if (!after && changed.Length > 0) mark.TextDecorations = TextDecorations.Strikethrough;
        block.Inlines.Add(mark); block.Inlines.Add(new Run(suffix)); return block;
    }

    private static FrameworkElement ComparisonLine(string label, TextBlock text, string color)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(61) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush(color), Margin = new Thickness(0, 4, 10, 0) });
        Grid.SetColumn(text, 1); grid.Children.Add(text); return grid;
    }

    private void ChangeDecisions(Action mutation)
    {
        if (_session is null) return;
        var before = _session.Suggestions.ToDictionary(KeyFor, s => s.Decision); mutation();
        if (_session.Suggestions.Any(s => before[KeyFor(s)] != s.Decision)) _decisionHistory.Push(before);
        UpdateActions();
    }

    private void UndoDecisions()
    {
        if (_session is null || !_decisionHistory.TryPop(out var previous)) return;
        for (int i = 0; i < _session.Suggestions.Count; i++) _session.SetDecision(i, previous.GetValueOrDefault(KeyFor(_session.Suggestions[i]), SuggestionDecision.Pending));
        UpdateActions();
    }

    private void ShowView(bool preview)
    {
        _showPreview = preview;
        _suggestionScroll.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        _preview.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        _suggestionTab.Background = Brush(preview ? "#FFFFFF" : "#E0EEE4");
        _previewTab.Background = Brush(preview ? "#E0EEE4" : "#FFFFFF");
        _suggestionTab.FontWeight = preview ? FontWeights.Normal : FontWeights.SemiBold;
        _previewTab.FontWeight = preview ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ShowEmpty(string title, string description)
    {
        _suggestions.Children.Clear();
        var message = new StackPanel { Margin = new Thickness(22, 30, 22, 22) };
        message.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        message.Children.Add(new TextBlock { Text = description, FontSize = 14, Foreground = Brush("#708076"), LineHeight = 24, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        _suggestions.Children.Add(new Border { Child = message, Background = Brush("#EDF3ED"), CornerRadius = new CornerRadius(12) });
    }

    private void CopyAccepted()
    {
        try
        {
            if (_session is null || _work is not null || !_complete) return;
            System.Windows.Clipboard.SetText(_session.BuildAccepted()); _status.Text = "채택한 수정을 반영한 글을 복사했어요. 원래 앱에서 Ctrl+V로 붙여넣으세요.";
        }
        catch { _status.Text = "클립보드를 사용할 수 없어요. 다시 시도해 주세요."; }
    }

    private async Task ApplyAsync()
    {
        if (_session is null || _snapshot is null || _work is not null || !_complete) return;
        var revised = _session.BuildAccepted(); _apply.IsEnabled = false;
        try
        {
            var result = await _write(_snapshot, revised);
            if (_closed) return;
            if (result.Success) { _snapshot = null; _source.Text = revised; }
            _status.Text = result.Message;
        }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { if (!_closed) UpdateActions(); }
    }

    private void UpdateActions()
    {
        if (_copy is null || _apply is null) return;
        var reviewable = _session is not null;
        var ready = _work is null && reviewable && _complete;
        int count = _session?.Suggestions.Count ?? 0, accepted = _session?.Suggestions.Count(s => s.Decision == SuggestionDecision.Accepted) ?? 0;
        _copy.IsEnabled = ready && (accepted > 0 || count == 0);
        _apply.IsEnabled = ready && _snapshot?.CanApply == true && _session!.BuildAccepted() != _session.Original;
        _apply.ToolTip = _snapshot?.CanApply == true ? "채택한 수정 사항을 원래 입력칸에 반영합니다." : "이 앱에서는 수정문을 복사해 붙여넣으세요.";
        _acceptAll.IsEnabled = reviewable && count > accepted;
        _clear.IsEnabled = reviewable && _session!.Suggestions.Any(s => s.Decision != SuggestionDecision.Pending);
        _undo.IsEnabled = reviewable && _decisionHistory.Count > 0;
        _previewTab.IsEnabled = reviewable;
        _suggestionTab.Content = count > 0 ? $"수정 제안 {count}" : "수정 제안";
        _summary.Text = _session is null ? (_work is not null ? "검사 중" : "검사 대기") : count == 0 ? (_complete ? "검사 완료" : "일부 검사") : $"{accepted}/{count}개 채택" + (_complete ? "" : " · 일부 검사");
        _preview.Text = _session?.BuildAccepted() ?? "";
        for (int i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i]; var decision = _session!.Suggestions[i].Decision;
            card.Label.Text = decision switch { SuggestionDecision.Accepted => "✓ 채택됨", SuggestionDecision.Skipped => "넘김", _ => "검토 대기" };
            card.Label.Foreground = Brush(decision == SuggestionDecision.Accepted ? "#24664F" : "#758278");
            card.Shell.BorderBrush = Brush(decision == SuggestionDecision.Accepted ? "#86BC98" : "#DCE5DD");
            card.Shell.Background = Brush(decision == SuggestionDecision.Accepted ? "#FAFDFA" : "#FFFFFF");
            card.Accept.Content = decision == SuggestionDecision.Accepted ? "채택됨" : "채택";
            card.Accept.IsEnabled = reviewable && decision != SuggestionDecision.Accepted;
            card.Skip.IsEnabled = reviewable && decision != SuggestionDecision.Skipped;
        }
    }

    internal async Task RunSmokeAsync(string path)
    {
        await ReviewAsync();
        if (_session is null || _session.Suggestions.Count == 0) throw new InvalidOperationException("Quick UI: expected corrections.");
        var original = _session.Original;
        if (_cards.Count != _session.Suggestions.Count) throw new InvalidOperationException("Quick UI: missing correction cards.");
        var fullResult = new ReviewResult(original, _session.Suggestions, "UI test");
        _complete = false;
        MergeResult(fullResult with { Suggestions = fullResult.Suggestions.Take(1).ToArray() });
        _cards[0].Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var firstCard = _cards[0];
        if (_copy.IsEnabled || _apply.IsEnabled) throw new InvalidOperationException("Quick UI: partial text cannot be copied or applied as complete.");
        MergeResult(fullResult);
        if (!ReferenceEquals(firstCard, _cards[0]) || _session!.Suggestions[0].Decision != SuggestionDecision.Accepted)
            throw new InvalidOperationException("Quick UI: incremental result replaced the card or lost its decision.");
        _complete = true;
        _clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _decisionHistory.Clear(); UpdateActions();
        _cards[0].Accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_session.BuildAccepted() == original || !_copy.IsEnabled || _preview.Text != _session.BuildAccepted()) throw new InvalidOperationException("Quick UI: accepting a card must update preview and copy output.");
        if (_apply.IsEnabled) throw new InvalidOperationException("Quick UI: copy-only source cannot enable native apply.");
        _undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_session.BuildAccepted() != original || _copy.IsEnabled) throw new InvalidOperationException("Quick UI: undo did not restore pending decision.");
        _acceptAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var all = _session.BuildAccepted();
        _clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_session.BuildAccepted() != original) throw new InvalidOperationException("Quick UI: clear must restore original.");
        _undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_session.BuildAccepted() != all || _preview.Text != all) throw new InvalidOperationException("Quick UI: one undo must restore all decisions after clear.");
        _cards[0].Skip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (_session.Suggestions[0].Decision != SuggestionDecision.Skipped || _preview.Text != _session.BuildAccepted()) throw new InvalidOperationException("Quick UI: skipping must update preview.");
        ShowView(true); UpdateLayout();
        if (!_showPreview || _preview.Visibility != Visibility.Visible || _suggestionScroll.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Quick UI: result tab did not switch.");
        ShowView(false); UpdateLayout();
        var surface = (FrameworkElement)Content;
        foreach (var control in new FrameworkElement[] { _status, _copy, _apply, _review, _mode, _summary })
        {
            var bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
            if (bounds.Bottom > surface.ActualHeight + 1 || bounds.Right > surface.ActualWidth + 1) throw new InvalidOperationException($"Quick UI: clipped {control.GetType().Name} {bounds} in {surface.ActualWidth}x{surface.ActualHeight}.");
        }
        if (_suggestionScroll.ActualHeight < 130) throw new InvalidOperationException("Quick UI: correction list is too short.");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(surface), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        bitmap.Render(drawing);
        using (var stream = System.IO.File.Create(path))
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }
        _source.Text += " 새 원문";
        if (_session is not null || _copy.IsEnabled) throw new InvalidOperationException("Quick UI: stale result survived source edit.");
        var editedSource = _source.Text;
        await LoadCaptureAsync(new CaptureSnapshot("뒤늦게 가져온 글", "test", "selection"));
        if (_source.Text != editedSource || _session is not null) throw new InvalidOperationException("Quick UI: late capture overwrote user input.");
    }

    private static string SourceLabel(string text) => $"원문 보기 · {text.Length:N0}자";
    private static CorrectionKey KeyFor(Suggestion suggestion) => new(suggestion.Start, suggestion.Length, suggestion.Original, suggestion.Replacement);
    private sealed record CorrectionKey(int Start, int Length, string Original, string Replacement);
    private sealed record CorrectionCard(CorrectionKey Key, Border Shell, TextBlock Label, Button Accept, Button Skip);
    private static Button MakeButton(string text, RoutedEventHandler click, bool primary = false, bool small = false)
    {
        var button = new Button { Content = text, Padding = small ? new Thickness(11, 6, 11, 6) : new Thickness(13, 9, 13, 9), FontSize = small ? 12 : 14, Margin = new Thickness(5, 0, 0, 0), MinHeight = small ? 30 : 38 };
        if (primary) { button.Background = Brush("#24664F"); button.Foreground = Brushes.White; button.BorderBrush = Brush("#24664F"); button.FontWeight = FontWeights.SemiBold; }
        button.Click += click; return button;
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
