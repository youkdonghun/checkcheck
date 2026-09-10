using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckCheck.App.Native;
using CheckCheck.Core;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;

namespace CheckCheck.App;

public partial class MainWindow : Window
{
    private readonly RuleProofreader _rules = new();
    private readonly LocalModelProofreader _local = new();
    private readonly NativeTextBridge _native = new();
    private ReviewSession? _session;
    private CaptureSnapshot? _snapshot;
    private CancellationTokenSource? _work;
    private bool _ready, _busy, _preparing, _settingSource, _step;
    private int _stepIndex;
    private readonly List<string> _protectedWords = [];
    private string _bareunKey = "";
    private uint _hotkeyModifiers = 3, _hotkeyKey = 0x20;
    private bool _popupEnabled = true, _preferCompact = true;
    private SelectionPopupWatcher? _popupWatcher;
    private QuickReviewWindow? _quick;
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CheckCheck", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        _ready = true;
        NaturalMode.IsEnabled = BusinessMode.IsEnabled = BareunEngine.IsChecked != true;
        Loaded += async (_, _) =>
        {
            if (App.IsTestRun || LocalEngine.IsChecked != true) return;
            if (!_local.IsInstalled) await PrepareLocalAiAsync();
            else await WarmInstalledAiAsync();
        };
        Loaded += async (_, _) => await CheckUpdateAsync(false);
        PreviewKeyDown += HandleKeys;
        SourceInitialized += (_, _) =>
        {
            try { _native.RegisterHotKeyAsync(this, CaptureSelectionAsync, _hotkeyModifiers, _hotkeyKey); }
            catch { SetStatus("전역 단축키를 등록하지 못했어요. 클립보드에서 가져오기는 사용할 수 있어요.", true); }
            ConfigurePopupWatcher();
        };
        Closed += (_, _) => { _work?.Cancel(); _warmupCancel.Cancel(); _quick?.Close(); _launcher?.Close(); _popupWatcher?.Dispose(); _native.Dispose(); _local.Dispose(); };
        _native.HotkeyFailed += ex => SetStatus(ex.Message, true);
        ShortcutLabel.Text = ShortcutText(_hotkeyModifiers, _hotkeyKey);
        UpdateEngineStatus();
        UpdateViewButtons();
    }

    private ReviewMode Mode => BusinessMode.IsChecked == true ? ReviewMode.Business : NaturalMode.IsChecked == true ? ReviewMode.Natural : ReviewMode.Minimal;

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ModeSubtitle.Text = Mode switch
        {
            ReviewMode.Natural => "전하려는 뜻과 말투를 살리면서 어색한 표현을 다듬어요.",
            ReviewMode.Business => "기한과 사실을 유지하면서 정중한 업무 표현으로 다듬어요.",
            _ => "틀린 부분만 고치고, 내 말투는 그대로 유지해요."
        };
        InvalidateReview();
        SetStatus("검사 방식을 바꿨어요. 검사하기를 눌러 새 수정안을 확인하세요.");
    }

    private void EngineChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (BareunEngine.IsChecked == true) MinimalMode.IsChecked = true;
        NaturalMode.IsEnabled = BusinessMode.IsEnabled = BareunEngine.IsChecked != true;
        InvalidateReview();
        UpdateEngineStatus();
        SaveSettings();
        SetStatus(BareunEngine.IsChecked == true ? "바른 API는 한국어 교정 전용이에요. 검사할 글을 바른 서버에 전송합니다. API 키는 설정에서 입력해 주세요." : "로컬 AI로 한글·영문 문장을 검사해요. 글은 이 PC에서만 처리합니다.");
        if (LocalEngine.IsChecked == true && _local.IsInstalled && !App.IsTestRun) _ = WarmInstalledAiAsync();
    }

    private void UpdateEngineStatus()
    {
        SourceEditor.MaxLength = LocalEngine.IsChecked == true ? 3000 : 10000;
        CharacterCount.Text = $"{SourceEditor.Text.Length:N0} / {SourceEditor.MaxLength:N0}자";
        EngineStatus.Text = BareunEngine.IsChecked == true ? (string.IsNullOrWhiteSpace(_bareunKey) ? "API 키를 설정해 주세요" : "한국어 교정 · 클라우드") : _local.IsReady ? "로컬 AI · 준비됨" : _warming ? "AI 미리 불러오는 중…" : _local.IsInstalled ? "검사할 때 AI를 준비해요" : "첫 실행 시 모델 자동 다운로드";
        SetupButton.Content = _local.IsInstalled ? "AI 확인" : "다운로드 시작";
        PrivacyTitle.Text = BareunEngine.IsChecked == true ? "바른 API로 검사" : "내 PC에서만 검사";
        PrivacyDescription.Text = BareunEngine.IsChecked == true ? "검사할 글을 바른 서버에\n전송해 교정합니다." : "입력한 글은 외부로\n전송하지 않아요.";
    }

    private async void ReviewClick(object sender, RoutedEventArgs e) => await ReviewAsync();

    private async Task ReviewAsync()
    {
        if (_busy) return;
        string text = SourceEditor.Text;
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("먼저 검사할 글을 입력해 주세요.", true); SourceEditor.Focus(); return; }
        if (text.Length > 10000) { SetStatus("한 번에 10,000자까지 검사할 수 있어요. 글을 나눠서 검사해 주세요.", true); return; }
        if (BareunEngine.IsChecked == true && string.IsNullOrWhiteSpace(_bareunKey)) { ApiSetupClick(this, new RoutedEventArgs()); return; }
        if (LocalEngine.IsChecked == true && !App.IsTestRun && !_local.IsInstalled && !await PrepareLocalAiAsync()) return;
        // Preparation allows editing. Capture the latest input before starting inference.
        text = SourceEditor.Text;
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("AI 준비가 끝났어요. 검사할 글을 입력해 주세요."); return; }
        StartWork("문장을 확인하고 있어요…");
        var token = _work!.Token;
        try
        {
            using var cloud = BareunEngine.IsChecked == true ? new BareunProofreader(_bareunKey) : null;
            var provider = App.IsTestRun ? (IProofreader)_rules : cloud is not null ? (IProofreader)cloud : _local;
            var response = await provider.ReviewAsync(text, Mode, ProgressReporter(), token);
            token.ThrowIfCancellationRequested();
            if (SourceEditor.Text != text) { SetStatus("원문이 바뀌어서 이전 검사 결과를 적용하지 않았어요.", true); return; }
            var filtered = response.Suggestions.Where(s => !_protectedWords.Any(word => s.Original.Contains(word, StringComparison.OrdinalIgnoreCase) && !s.Replacement.Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
            _session = new ReviewSession(text, filtered);
            _stepIndex = 0;
            RenderSession();
            string countMessage = filtered.Length == 0 ? "이번 검사에서 수정 제안을 찾지 못했어요. 모든 문장이 정확하다는 뜻은 아니에요." : $"수정 제안 {filtered.Length}개를 찾았어요. 확인한 내용만 선택해 주세요.";
            if (filtered.Length < response.Suggestions.Count) countMessage += " 보호할 단어가 바뀌는 제안은 제외했어요.";
            SetStatus(countMessage + (string.IsNullOrEmpty(response.Note) ? "" : " " + response.Note));
        }
        catch (OperationCanceledException) { SetStatus("검사를 취소했어요. 원문은 그대로 유지됩니다."); }
        catch (Exception ex) { SetStatus(FriendlyError(ex), true); }
        finally { FinishWork(); UpdateEngineStatus(); }
    }

    private async void SetupClick(object sender, RoutedEventArgs e) => await PrepareLocalAiAsync();

    private async Task<bool> PrepareLocalAiAsync()
    {
        if (_busy) return false;
        LocalEngine.IsChecked = true;
        _preparing = true;
        StartWork("로컬 AI를 준비하고 있어요…");
        ShowPreparationHint();
        try
        {
            await _local.EnsureReadyAsync(ProgressReporter(), _work!.Token);
            SetStatus("로컬 AI 준비가 끝났어요. 한글·영문 문장을 입력하고 검사하기를 눌러주세요.");
            return true;
        }
        catch (OperationCanceledException) { SetStatus("AI 준비를 중지했어요. ‘다운로드 시작’을 누르면 이어서 준비해요."); return false; }
        catch (Exception ex) { SetStatus("AI 준비를 완료하지 못했어요. " + FriendlyError(ex) + " ‘다운로드 시작’으로 재시도할 수 있어요.", true); return false; }
        finally
        {
            _preparing = false;
            FinishWork(); UpdateEngineStatus();
            if (_session is null)
            {
                ((TextBlock)EmptyPanel.Children[0]).Text = _local.IsInstalled ? "로컬 AI로 문장을 확인할 준비가 됐어요." : "로컬 AI 준비가 아직 끝나지 않았어요.";
                ((TextBlock)EmptyPanel.Children[1]).Text = _local.IsInstalled ? "원문을 입력하고 검사하기를 눌러주세요." : "다운로드를 다시 시작하면 이어서 준비합니다. API 키와 사용료는 필요 없어요.";
            }
        }
    }

    private void ShowPreparationHint()
    {
        if (_session is not null) return;
        ((TextBlock)EmptyPanel.Children[0]).Text = "처음 한 번, 무료 로컬 AI를 준비해요.";
        ((TextBlock)EmptyPanel.Children[1]).Text = "모델 약 2.4GB · 창을 닫아도 다운로드는 계속돼요.\n기다리는 동안 검사할 글을 입력해 두세요.";
    }

    private IProgress<EngineProgress> ProgressReporter() => new Progress<EngineProgress>(p =>
    {
        if (!_busy) return;
        SetStatus(p.Message);
        WorkProgress.IsIndeterminate = !p.Fraction.HasValue;
        if (p.Fraction.HasValue) WorkProgress.Value = Math.Clamp(p.Fraction.Value * 100, 0, 100);
    });

    private void StartWork(string message)
    {
        _busy = true;
        _work = new CancellationTokenSource();
        SourceEditor.IsReadOnly = !_preparing;
        ReviewButton.IsEnabled = false;
        ReviewButton.Content = _preparing ? "AI 준비 중…" : "검사 중…";
        SetupButton.IsEnabled = false;
        ApiSetupButton.IsEnabled = false;
        MinimalMode.IsEnabled = NaturalMode.IsEnabled = BusinessMode.IsEnabled = false;
        LocalEngine.IsEnabled = BareunEngine.IsEnabled = false;
        ApplyButton.IsEnabled = CopyButton.IsEnabled = AcceptAllButton.IsEnabled = UndoButton.IsEnabled = false;
        CancelButton.Visibility = WorkProgress.Visibility = Visibility.Visible;
        CancelButton.Content = _preparing ? "준비 중지" : "취소";
        WorkProgress.IsIndeterminate = true;
        SetStatus(message);
    }

    private void FinishWork()
    {
        _busy = false;
        _work?.Dispose(); _work = null;
        SourceEditor.IsReadOnly = false;
        ReviewButton.IsEnabled = SetupButton.IsEnabled = ApiSetupButton.IsEnabled = true;
        ReviewButton.Content = "검사하기  →";
        MinimalMode.IsEnabled = NaturalMode.IsEnabled = BusinessMode.IsEnabled = true;
        NaturalMode.IsEnabled = BusinessMode.IsEnabled = BareunEngine.IsChecked != true;
        LocalEngine.IsEnabled = BareunEngine.IsEnabled = true;
        CancelButton.Visibility = WorkProgress.Visibility = Visibility.Collapsed;
        UpdateDecisionButtons();
    }

    private void CancelClick(object sender, RoutedEventArgs e) => _work?.Cancel();

    private void SourceChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        CharacterCount.Text = $"{SourceEditor.Text.Length:N0} / {SourceEditor.MaxLength:N0}자";
        if (!_settingSource) { _snapshot = null; SourceLabel.Text = "직접 입력 · 한글 / English"; }
        InvalidateReview();
        if (_preparing) ShowPreparationHint();
    }

    private void InvalidateReview()
    {
        _session = null;
        SuggestionPanel.Children.Clear(); EmptyPanel.Visibility = Visibility.Visible;
        SuggestionTitle.Text = "수정 제안";
        PreviewEditor.Document = CreateDocument();
        PreviewEditor.Document.Blocks.Add(new Paragraph(new Run("검사하면 수정안을 여기서 확인할 수 있어요.") { Foreground = Brush("#98A396") }));
        DecisionStatus.Text = "검사한 뒤 반영할 수정 사항을 선택해 주세요.";
        UpdateDecisionButtons();
    }

    private void SampleClick(object sender, RoutedEventArgs e)
    {
        if (_busy && !_preparing) return;
        SetSource("안녕하세요. 보내주신 자료 확인햇어요.\n몇일 전에 말씀드린 회의 일정은 내일 오후 3시입니다.\n검토한 내용을 오늘까지 답변 주세요. 감사합니다.\n\nI has recieved your email. Please send me the updated report by Friday.", null);
        SetStatus("예문을 불러왔어요. 검사하기를 눌러보세요.");
    }

    private void PasteClick(object sender, RoutedEventArgs e)
    {
        if (_busy && !_preparing) return;
        try
        {
            if (!Clipboard.ContainsText()) { SetStatus("클립보드에 텍스트가 없어요.", true); return; }
            string text = Clipboard.GetText();
            if (text.Length > 10000) { SetStatus("복사한 글이 10,000자를 넘어요. 필요한 부분만 다시 복사해 주세요.", true); return; }
            SetSource(text, null); SetStatus("클립보드의 글을 가져왔어요.");
        }
        catch { SetStatus("다른 앱이 클립보드를 사용 중이에요. 잠시 후 다시 시도해 주세요.", true); }
    }

    private void SetSource(string text, CaptureSnapshot? snapshot)
    {
        _settingSource = true;
        try { SourceEditor.Text = text; }
        finally { _settingSource = false; }
        _snapshot = snapshot;
        SourceLabel.Text = snapshot is null ? "직접 입력 · 한글 / English" : $"{snapshot.DisplayName} · {snapshot.ScopeLabel}";
        UpdateDecisionButtons();
    }

    private async Task CaptureSelectionAsync()
    {
        if (_busy && !_preparing) { BringToFront(); return; }
        try
        {
            var captured = await _native.CaptureAsync();
            if (captured is null) { ShowCaptureFallback("선택한 글을 읽지 못했어요. 글을 복사한 뒤 붙여넣기로 검사해 주세요."); return; }
            if (_preferCompact && !_preparing) { OpenQuick(captured.Text, captured, false); return; }
            BringToFront();
            SetSource(captured.Text, captured);
            SetStatus(captured.CanApply ? "선택한 글을 가져왔어요. 검토 후 원래 입력칸에 반영할 수 있어요." : "글을 가져왔어요. 이 앱에서는 교정문을 복사해서 붙여넣어 주세요.");
        }
        catch (Exception ex) { ShowCaptureFallback(FriendlyError(ex)); }
    }

    private void ShowCaptureFallback(string message)
    {
        if (_preferCompact && !_preparing) { OpenQuick("", null, false); _quick?.SetNotice(message); }
        else { BringToFront(); SetStatus(message, true); }
    }

    private void BringToFront() { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Show(); Activate(); }

    private void RenderSession()
    {
        if (_session is null) return;
        RenderPreview();
        SuggestionPanel.Children.Clear();
        EmptyPanel.Visibility = _session.Suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_session.Suggestions.Count == 0)
        {
            ((TextBlock)EmptyPanel.Children[0]).Text = "이번 검사에서 발견한 수정 제안이 없어요.";
            ((TextBlock)EmptyPanel.Children[1]).Text = "다른 검사 방식으로 다시 확인할 수도 있어요.";
        }
        else
        {
            ((TextBlock)EmptyPanel.Children[0]).Text = "작은 실수까지, 한 번 더 체크.";
            ((TextBlock)EmptyPanel.Children[1]).Text = "원문을 입력하거나 선택한 글을 가져와 보세요.";
        }
        SuggestionTitle.Text = $"수정 제안  {_session.Suggestions.Count}";
        var indices = _step ? new[] { Math.Clamp(_stepIndex, 0, Math.Max(0, _session.Suggestions.Count - 1)) } : Enumerable.Range(0, _session.Suggestions.Count).ToArray();
        foreach (var index in indices)
        {
            if (index >= _session.Suggestions.Count) continue;
            var s = _session.Suggestions[index];
            var row = new Grid { Margin = new Thickness(0, 9, 0, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            content.Children.Add(new TextBlock { Text = $"{index + 1}. {s.Category}" + (_step ? $"   ·   {index + 1} / {_session.Suggestions.Count}" : ""), FontSize = 10, Foreground = Brush("#849182"), Margin = new Thickness(0, 0, 0, 5) });
            var change = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            change.Inlines.Add(new Run(DisplayWhitespace(s.Original)) { Foreground = Brush("#9F6254"), TextDecorations = TextDecorations.Strikethrough });
            change.Inlines.Add(new Run("  →  ") { Foreground = Brush("#93A18E") });
            change.Inlines.Add(new Run(DisplayWhitespace(s.Replacement)) { Foreground = Brush("#286346"), FontWeight = FontWeights.SemiBold });
            content.Children.Add(change);
            content.Children.Add(new TextBlock { Text = s.Reason, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#7B8779"), Margin = new Thickness(0, 5, 0, 0) });
            row.Children.Add(content);
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (s.Decision == SuggestionDecision.Pending)
            {
                var accept = new Button { Content = "바꾸기", Padding = new Thickness(11, 7, 11, 7), Background = Brush("#E7F2E5"), Foreground = Brush("#326949"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 5, 0) };
                accept.Click += (_, _) => Decide(index, SuggestionDecision.Accepted);
                var skip = new Button { Content = "넘기기", Padding = new Thickness(10, 7, 10, 7) };
                skip.Click += (_, _) => Decide(index, SuggestionDecision.Skipped);
                actions.Children.Add(accept); actions.Children.Add(skip);
            }
            else
            {
                var reopen = new Button { Content = s.Decision == SuggestionDecision.Accepted ? "✓ 선택됨 · 취소" : "넘김 · 다시 보기", Style = (Style)FindResource("QuietButton") };
                reopen.Click += (_, _) => Decide(index, SuggestionDecision.Pending);
                actions.Children.Add(reopen);
            }
            Grid.SetColumn(actions, 1); row.Children.Add(actions);
            SuggestionPanel.Children.Add(row);
            if (!_step && index < _session.Suggestions.Count - 1) SuggestionPanel.Children.Add(new Border { Height = 1, Background = Brush("#EEF1E9") });
        }
        if (_step && _session.Suggestions.Count > 0)
        {
            var nav = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 6) };
            var previous = new Button { Content = "← 이전", Style = (Style)FindResource("QuietButton"), IsEnabled = _stepIndex > 0 };
            var next = new Button { Content = "다음 →", Style = (Style)FindResource("QuietButton"), IsEnabled = _stepIndex < _session.Suggestions.Count - 1 };
            previous.Click += (_, _) => { _stepIndex--; RenderSession(); };
            next.Click += (_, _) => { _stepIndex++; RenderSession(); };
            nav.Children.Add(previous); nav.Children.Add(next); SuggestionPanel.Children.Add(nav);
        }
        int accepted = _session.Suggestions.Count(s => s.Decision == SuggestionDecision.Accepted);
        int pending = _session.Suggestions.Count(s => s.Decision == SuggestionDecision.Pending);
        DecisionStatus.Text = $"선택 {accepted}개 · 남은 제안 {pending}개";
        PreviewLegend.Text = pending > 0 ? "미검토 제안 포함 · 선택한 수정 사항만 복사·반영돼요" : "검토 완료 · 선택한 수정 사항만 미리보기에 반영됐어요";
        UpdateDecisionButtons();
    }

    private void RenderPreview()
    {
        if (_session is null) return;
        var doc = CreateDocument(); var paragraph = new Paragraph { LineHeight = 27 };
        int cursor = 0;
        foreach (var s in _session.Suggestions)
        {
            if (s.Start > cursor) paragraph.Inlines.Add(new Run(_session.Original[cursor..s.Start]));
            if (s.Decision == SuggestionDecision.Skipped) paragraph.Inlines.Add(new Run(s.Original));
            else if (s.Replacement.Length > 0) paragraph.Inlines.Add(new Run(s.Replacement) { Background = Brush(s.Decision == SuggestionDecision.Accepted ? "#D7ECCC" : "#E8F3E3"), Foreground = Brush("#285D3D"), TextDecorations = TextDecorations.Underline });
            else paragraph.Inlines.Add(new Run("⌫") { Foreground = Brush("#A36F59"), ToolTip = "삭제: " + s.Original });
            cursor = s.Start + s.Length;
        }
        if (cursor < _session.Original.Length) paragraph.Inlines.Add(new Run(_session.Original[cursor..]));
        doc.Blocks.Add(paragraph); PreviewEditor.Document = doc;
    }

    private static FlowDocument CreateDocument() => new() { PagePadding = new Thickness(3), FontFamily = new FontFamily("Malgun Gothic"), FontSize = 15 };
    private static string DisplayWhitespace(string value) => value.Length == 0 ? "(없음)" : string.IsNullOrWhiteSpace(value) ? value.Replace(" ", "␣").Replace("\r", "").Replace("\n", "↵") : value.Replace("\r", "").Replace("\n", " ↵ ");
    private static SolidColorBrush Brush(string hex) => new((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void Decide(int index, SuggestionDecision decision)
    {
        if (_session is null || _busy) return;
        _session.SetDecision(index, decision);
        if (_step && decision != SuggestionDecision.Pending)
        {
            int next = Enumerable.Range(index + 1, _session.Suggestions.Count - index - 1).FirstOrDefault(i => _session.Suggestions[i].Decision == SuggestionDecision.Pending, -1);
            if (next >= 0) _stepIndex = next;
        }
        RenderSession();
    }
    private void AcceptAllClick(object sender, RoutedEventArgs e) { if (_session is null || _busy) return; _session.AcceptAll(); RenderSession(); }
    private void UndoClick(object sender, RoutedEventArgs e) { if (_session is null || _busy) return; _session.Undo(); RenderSession(); }
    private void CompareClick(object sender, RoutedEventArgs e) { _step = false; UpdateViewButtons(); RenderSession(); }
    private void StepClick(object sender, RoutedEventArgs e) { _step = true; _stepIndex = 0; UpdateViewButtons(); RenderSession(); }
    private void UpdateViewButtons() { CompareButton.Background = Brush(_step ? "#F7F8F4" : "#E5EEDF"); StepButton.Background = Brush(_step ? "#E5EEDF" : "#F7F8F4"); }
    private void UpdateDecisionButtons()
    {
        bool has = _session is not null && !_busy;
        CopyButton.IsEnabled = has;
        AcceptAllButton.IsEnabled = has && _session!.Suggestions.Any(s => s.Decision == SuggestionDecision.Pending);
        UndoButton.IsEnabled = has && _session!.CanUndo;
        ApplyButton.IsEnabled = has && _snapshot?.CanApply == true && _session!.BuildAccepted() != _session.Original;
        ApplyButton.ToolTip = _snapshot?.CanApply == true ? "검토한 수정 사항을 원래 입력칸에 반영" : "전역 단축키로 지원되는 일반 입력칸의 글을 가져오면 사용할 수 있어요.";
    }
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _busy) return;
        try { Clipboard.SetText(_session.BuildAccepted()); SetStatus("선택한 수정 사항을 반영한 글을 복사했어요. 원하는 곳에 붙여넣으세요."); }
        catch { SetStatus("클립보드에 복사하지 못했어요. 잠시 후 다시 시도해 주세요.", true); }
    }
    private async void ApplyClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _snapshot?.CanApply != true || _busy) return;
        string replacement = _session.BuildAccepted();
        StartWork("원래 입력칸과 원문을 확인하고 있어요…");
        CancelButton.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _native.ApplyAsync(_snapshot, replacement);
            if (result.Success) { SetSource(replacement, null); SetStatus(result.Message); }
            else SetStatus(result.Message, true);
        }
        catch (Exception ex) { SetStatus(FriendlyError(ex), true); }
        finally { FinishWork(); BringToFront(); }
    }
    private void SetStatus(string message, bool error = false) { StatusText.Text = message; StatusText.Foreground = Brush(error ? "#AA5D42" : "#72836F"); }
    private static string FriendlyError(Exception ex) => ex is HttpRequestException ? "연결하지 못했어요. 다운로드 중이라면 인터넷 연결을 확인하고 다시 시도해 주세요." : string.IsNullOrWhiteSpace(ex.Message) ? "작업을 완료하지 못했어요. 다시 시도해 주세요." : ex.Message;
    private async void HandleKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await ReviewAsync(); }
        else if (e.Key == Key.Escape && _busy) { e.Handled = true; _work?.Cancel(); }
    }
    protected override void OnClosing(CancelEventArgs e) { SaveSettings(); base.OnClosing(e); if (!e.Cancel) _work?.Cancel(); }

    private void DictionaryClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var window = new Window { Owner = this, Title = "보호할 단어 · 체크체크", Width = 430, Height = 420, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F7F8F4") };
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "이름이나 제품명을 한 줄에 하나씩 적어주세요.\n등록한 표현을 삭제하거나 바꾸는 제안은 제외해요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var editor = new TextBox { Text = string.Join(Environment.NewLine, _protectedWords), AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 10000 };
        Grid.SetRow(editor, 1); grid.Children.Add(editor);
        var save = new Button { Content = "저장", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0), MinWidth = 90 };
        save.Click += (_, _) => { _protectedWords.Clear(); _protectedWords.AddRange(editor.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200)); SaveSettings(); InvalidateReview(); SetStatus("보호할 단어를 저장했어요. 다시 검사하면 적용돼요."); window.Close(); };
        Grid.SetRow(save, 2); grid.Children.Add(save); window.Content = grid; window.ShowDialog();
    }
    private sealed class UserSettings { public int Version { get; set; } public bool UseLocalAi { get; set; } public bool UseBareun { get; set; } public uint HotkeyModifiers { get; set; } = 3; public uint HotkeyKey { get; set; } = 0x20; public bool PopupEnabled { get; set; } = true; public bool PreferCompact { get; set; } = true; public string? EncryptedBareunKey { get; set; } public List<string> ProtectedWords { get; set; } = []; }
    private static bool SelectLocalAi(UserSettings settings) => !settings.UseBareun;
    private void LoadSettings()
    {
        if (App.IsTestRun) return;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath));
            if (settings is null) return;
            if (ValidShortcut(settings.HotkeyModifiers, settings.HotkeyKey)) { _hotkeyModifiers = settings.HotkeyModifiers; _hotkeyKey = settings.HotkeyKey; }
            _popupEnabled = settings.PopupEnabled; _preferCompact = settings.PreferCompact;
            _protectedWords.AddRange(settings.ProtectedWords.Where(w => !string.IsNullOrWhiteSpace(w)).Take(200));
            if (!string.IsNullOrEmpty(settings.EncryptedBareunKey))
            {
                try { _bareunKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.EncryptedBareunKey), null, DataProtectionScope.CurrentUser)); }
                catch { _bareunKey = ""; }
            }
            LocalEngine.IsChecked = SelectLocalAi(settings);
            BareunEngine.IsChecked = settings.UseBareun;
            if (settings.UseBareun) MinimalMode.IsChecked = true;
        }
        catch { /* A missing or damaged preference file never prevents startup. */ }
    }
    private void SaveSettings()
    {
        if (App.IsTestRun) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UserSettings { Version = 2, UseLocalAi = LocalEngine.IsChecked == true, UseBareun = BareunEngine.IsChecked == true, HotkeyModifiers = _hotkeyModifiers, HotkeyKey = _hotkeyKey, PopupEnabled = _popupEnabled, PreferCompact = _preferCompact, EncryptedBareunKey = string.IsNullOrEmpty(_bareunKey) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(_bareunKey), null, DataProtectionScope.CurrentUser)), ProtectedWords = _protectedWords }, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* Preferences are optional; do not store document contents. */ }
    }

    private void ApiSetupClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Window { Owner = this, Title = "바른 API 설정 · 체크체크", Width = 475, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F7F8F4") };
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "한국어 맞춤법 · 띄어쓰기", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "바른 Free 요금제는 월 5만 어절로 안내되어 있어요. 가입 후 발급받은 본인 키를 입력하세요. 체크체크는 유료 결제나 요금제 변경을 실행하지 않아요.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
        var website = new Button { Content = "바른 가입·무료 요금제 확인 ↗", Style = (Style)FindResource("QuietButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 15) };
        website.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://bareun.ai/product") { UseShellExecute = true });
        panel.Children.Add(website);
        panel.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(_bareunKey) ? "API 키" : "API 키 · 저장된 키가 있어요", FontSize = 12, Margin = new Thickness(0, 0, 0, 7) });
        var keyInput = new PasswordBox { Padding = new Thickness(10), FontSize = 15, MaxLength = 256, Password = _bareunKey };
        panel.Children.Add(keyInput);
        panel.Children.Add(new TextBlock { Text = "키는 이 Windows 계정에서만 읽을 수 있게 암호화해 저장해요. 바른 API를 선택해 검사할 때 원문이 api.bareun.ai로 전송됩니다. 무료 한도와 계정 상태는 바른에서 확인해 주세요.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#778776"), Margin = new Thickness(0, 12, 0, 18) });
        var save = new Button { Content = "암호화해서 저장", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        save.Click += (_, _) => { _bareunKey = keyInput.Password.Trim(); SaveSettings(); UpdateEngineStatus(); SetStatus(string.IsNullOrEmpty(_bareunKey) ? "저장된 API 키를 지웠어요." : "API 키를 저장했어요. ‘바른 API’를 선택해 검사할 수 있어요."); dialog.Close(); };
        panel.Children.Add(save); dialog.Content = panel; dialog.ShowDialog();
    }

    internal async Task RunUiSmokeAsync(string imagePath)
    {
        if (LocalEngine.IsChecked != true) throw new InvalidOperationException("UI smoke: local AI must be the default.");
        if (!SelectLocalAi(new UserSettings()) || SelectLocalAi(new UserSettings { UseBareun = true }) || !SelectLocalAi(new UserSettings { Version = 2, UseLocalAi = false }))
            throw new InvalidOperationException("UI smoke: engine preference migration failed.");
        SourceEditor.Text = "자료 확인햇어요. 몇일 전에 보낸 메일을 검토해 주세요.\nI has recieved your email.";
        await ReviewAsync();
        if (_session is null || _session.Suggestions.Count < 2) throw new InvalidOperationException("UI smoke: no expected suggestions.");
        string original = SourceEditor.Text;
        _session.AcceptAll();
        if (_session.BuildAccepted() == original) throw new InvalidOperationException("UI smoke: accept did not change text.");
        _session.Undo();
        if (_session.BuildAccepted() != original) throw new InvalidOperationException("UI smoke: undo failed.");
        RenderSession();
        UpdateLayout();
        var surface = (FrameworkElement)Content;
        foreach (var element in new FrameworkElement[] { StatusText, ApplyButton, ReviewButton, PreviewEditor })
        {
            var bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Bottom > surface.ActualHeight + 1 || bounds.Right > surface.ActualWidth + 1)
                throw new InvalidOperationException("UI smoke: control clipped at current window size.");
        }
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
        using var stream = File.Create(imagePath); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        SourceEditor.Text += " 追加";
        if (_session is not null || ApplyButton.IsEnabled) throw new InvalidOperationException("UI smoke: stale session not cleared.");
    }
}
