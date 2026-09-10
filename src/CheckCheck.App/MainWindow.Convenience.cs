using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CheckCheck.App.Native;
using CheckCheck.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace CheckCheck.App;

public partial class MainWindow
{
    private QuickLauncherWindow? _launcher;
    private bool _popupCaptureBusy;
    internal CancellationToken BeginBackgroundLifecycleCheck() { _preparing = true; StartWork("테스트 중"); return _work!.Token; }
    internal void EndBackgroundLifecycleCheck() { _preparing = false; FinishWork(); }
    internal async Task RunQuickSmokeAsync(string imagePath)
    {
        const string validHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        if (AppUpdater.ReadHash(validHash + "  CheckCheck.exe\n") != validHash) throw new InvalidOperationException("Update checksum parsing failed.");
        var releaseJson = System.Text.Json.JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v99.0.0", assets = new[] {
            new { name = "CheckCheck.exe", size = 75000000, browser_download_url = "https://github.com/youkdonghun/checkcheck/releases/download/v99.0.0/CheckCheck.exe" },
            new { name = "SHA256SUMS.txt", size = 200, browser_download_url = "https://github.com/youkdonghun/checkcheck/releases/download/v99.0.0/SHA256SUMS.txt" } } });
        if (AppUpdater.ParseRelease(releaseJson)?.Version != new Version(99, 0, 0)) throw new InvalidOperationException("Update version parsing failed.");
        try { AppUpdater.ParseRelease(releaseJson.Replace("github.com/youkdonghun", "example.com/youkdonghun")); throw new InvalidOperationException("Foreign update URL accepted."); } catch (System.IO.InvalidDataException) { }
        if (AppUpdater.ParseRelease(releaseJson.Replace("v99.0.0", "v0.1.0")) is not null) throw new InvalidOperationException("Update downgrade offered.");
        if (ValidShortcut(0, 0x41) || !ValidShortcut(3, 0x20) || ValidShortcut(2, 0x10)) throw new InvalidOperationException("Shortcut validation failed.");
        if (SelectionPopupWatcher.IsExternal(0, 42) || SelectionPopupWatcher.IsExternal(42, (uint)Environment.ProcessId) || !SelectionPopupWatcher.IsExternal(42, uint.MaxValue)) throw new InvalidOperationException("Mouse target filtering failed.");
        foreach (bool temporary in new[] { false, true })
        {
            var quick = new QuickReviewWindow("몇일 전에 보낸 자료를 확인햇어요. I has recieved your email.", null, false,
                (text, mode, progress, token) => _rules.ReviewAsync(text, mode, progress, token), _native.ApplyAsync, (_, _) => { });
            quick.Height = temporary ? 480 : 540;
            quick.ShowInTaskbar = false; quick.WindowStartupLocation = WindowStartupLocation.Manual; quick.Left = -20000; quick.Top = -20000;
            quick.Show();
            try { await quick.RunSmokeAsync(imagePath + (temporary ? ".popup.png" : ".compact.png")); }
            finally { quick.Close(); }
        }
        var testWindow = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        testWindow.Show();
        await NativeSelfTest.RunHotkeyChecksAsync(testWindow);
        using var bridge = new NativeTextBridge();
        using var competitor = new NativeTextBridge();
        try
        {
            bridge.RegisterHotKey(this, () => { }, 3, 0x87);
            bool conflict = false;
            try { competitor.RegisterHotKey(testWindow, () => { }, 3, 0x87); } catch (InvalidOperationException) { conflict = true; }
            if (!conflict) throw new InvalidOperationException("Shortcut conflict not reported.");
            bridge.RegisterHotKey(this, () => { }, 3, 0x86);
            competitor.RegisterHotKey(testWindow, () => { }, 3, 0x87);
            try { bridge.RegisterHotKey(this, () => { }, 3, 0x87); throw new InvalidOperationException("Shortcut collision lost old registration."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("다른 앱")) { }
        }
        finally { testWindow.Close(); }
    }

    private void CompactClick(object sender, RoutedEventArgs e) => OpenQuick(SourceEditor.Text, _snapshot, false);

    private void OpenQuick(string text, CaptureSnapshot? snapshot, bool temporary, int x = 0, int y = 0)
    {
        _quick?.Close();
        _quick = new QuickReviewWindow(text, snapshot, temporary, ReviewQuickAsync, _native.ApplyAsync,
            (source, capture) => { SetSource(source, capture); BringToFront(); }, temporary ? ReviewMode.Minimal : Mode);
        if (temporary) _quick.PlaceNear(x, y);
        _quick.Show();
        if (!temporary) Hide();
    }

    private async Task<ReviewResult> ReviewQuickAsync(string text, ReviewMode mode, IProgress<EngineProgress> progress, CancellationToken token)
    {
        if (_preparing) throw new InvalidOperationException("로컬 AI를 내려받고 있어요. 크게 보기에서 진행률을 확인해 주세요.");
        if (!_local.IsInstalled) throw new InvalidOperationException("로컬 AI 준비가 필요해요. 크게 보기에서 ‘다운로드 시작’을 눌러주세요.");
        var response = await _local.ReviewAsync(text, mode, progress, token);
        var words = _protectedWords.ToArray();
        return response with { Suggestions = response.Suggestions.Where(s => !words.Any(w => s.Original.Contains(w, StringComparison.OrdinalIgnoreCase) && !s.Replacement.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray() };
    }

    private void ConfigurePopupWatcher()
    {
        _popupWatcher?.Dispose(); _popupWatcher = null;
        _launcher?.Close(); _launcher = null;
        if (!_popupEnabled || App.IsTestRun) return;
        try { _popupWatcher = new SelectionPopupWatcher(_native, Dispatcher, ShowLauncher, () => { _launcher?.Close(); _launcher = null; }); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void ShowLauncher(nint target, CaptureSnapshot? selected, int x, int y)
    {
        if (_native.SuspendHotkeyCallbacks || _popupCaptureBusy) return;
        _launcher?.Close();
        _launcher = new QuickLauncherWindow(selected is not null, async () => await CapturePopupAsync(target, x, y));
        _launcher.PlaceNear(x, y);
        _launcher.Show();
    }

    private async Task CapturePopupAsync(nint target, int x, int y)
    {
        if (_popupCaptureBusy) return;
        _popupCaptureBusy = true;
        try
        {
            CaptureSnapshot? capture = null;
            string notice = "선택한 글을 읽지 못했어요. 글을 복사한 뒤 ‘붙여넣기’를 눌러주세요.";
            try { capture = await _native.CaptureFromWindowAsync(target, selectedOnly: true); }
            catch (Exception ex) { notice = ex.Message + " 글을 복사한 뒤 붙여넣기로 검사할 수 있어요."; }
            OpenQuick(capture?.Text ?? "", capture, true, x, y);
            if (capture is null) _quick?.SetNotice(notice);
            _quick?.Activate();
        }
        finally { _popupCaptureBusy = false; }
    }

    private static bool ValidShortcut(uint modifiers, uint key) => modifiers is > 0 and < 16 && (modifiers & 3) != 0 &&
        (key == 0x20 || key is >= 0x30 and <= 0x5A || key is >= 0x70 and <= 0x87);

    private static string ShortcutText(uint modifiers, uint key) =>
        string.Join(" + ", new[] { (modifiers & 2) != 0 ? "Ctrl" : null, (modifiers & 1) != 0 ? "Alt" : null, (modifiers & 4) != 0 ? "Shift" : null, (modifiers & 8) != 0 ? "Win" : null, key == 0x20 ? "Space" : KeyInterop.KeyFromVirtualKey((int)key).ToString() }.Where(x => x is not null));

    private void PreferencesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Owner = this, Title = "단축키 · 편의 설정", Width = 450, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#F7F8F4") };
        var panel = new StackPanel { Margin = new Thickness(25) };
        panel.Children.Add(new TextBlock { Text = "내가 쓰기 편한 체크체크", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "글 가져오기 단축키", FontSize = 12 });
        uint modifiers = _hotkeyModifiers, key = _hotkeyKey;
        var shortcut = new TextBox { Text = ShortcutText(modifiers, key), IsReadOnly = true, Margin = new Thickness(0, 7, 0, 5) };
        var feedback = new TextBlock { Text = "여기를 누르고 원하는 키 조합을 누르세요.\nCtrl 또는 Alt를 포함한 문자·숫자·F키·Space 조합", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#72836F"), Margin = new Thickness(0, 0, 0, 15) };
        shortcut.PreviewKeyDown += (_, args) =>
        {
            args.Handled = true;
            var pressed = args.Key == Key.System ? args.SystemKey : args.Key;
            uint nextKey = (uint)KeyInterop.VirtualKeyFromKey(pressed);
            var current = Keyboard.Modifiers;
            uint nextModifiers = (uint)(((current & ModifierKeys.Alt) != 0 ? 1 : 0) | ((current & ModifierKeys.Control) != 0 ? 2 : 0) | ((current & ModifierKeys.Shift) != 0 ? 4 : 0) | ((current & ModifierKeys.Windows) != 0 ? 8 : 0));
            if (!ValidShortcut(nextModifiers, nextKey)) return;
            modifiers = nextModifiers; key = nextKey; shortcut.Text = ShortcutText(modifiers, key);
        };
        panel.Children.Add(shortcut); panel.Children.Add(feedback);
        var popup = new CheckBox { Content = "다른 앱에서 우클릭하면 검사 버튼 표시", IsChecked = _popupEnabled, Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(popup);
        panel.Children.Add(new TextBlock { Text = "마우스 근처의 체크체크 버튼을 누르면 선택한 글을 가져와요.\n읽을 수 없는 앱에서는 복사 후 붙여넣기를 이용하세요.", FontSize = 11, Foreground = Brush("#72836F"), Margin = new Thickness(23, 0, 0, 16), TextWrapping = TextWrapping.Wrap });
        var compact = new CheckBox { Content = "단축키로 가져올 때 작은 창으로 열기", IsChecked = _preferCompact, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(compact);
        panel.Children.Add(new TextBlock { Text = "창의 X는 백그라운드로 숨겨요.\n완전히 종료하려면 트레이 아이콘 → 체크체크 종료", FontSize = 11, Foreground = Brush("#72836F"), Margin = new Thickness(0, 0, 0, 18) });
        var save = new Button { Content = "설정 저장", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            try
            {
                if (modifiers != _hotkeyModifiers || key != _hotkeyKey) _native.RegisterHotKeyAsync(this, CaptureSelectionAsync, modifiers, key);
                _hotkeyModifiers = modifiers; _hotkeyKey = key; _popupEnabled = popup.IsChecked == true; _preferCompact = compact.IsChecked == true;
                ShortcutLabel.Text = ShortcutText(modifiers, key); ConfigurePopupWatcher(); SaveSettings();
                SetStatus("단축키와 편의 설정을 저장했어요. " + ShortcutLabel.Text + "로 불러오세요."); dialog.Close();
            }
            catch (Exception ex) { feedback.Text = ex.Message; feedback.Foreground = Brush("#AA5D42"); }
        };
        panel.Children.Add(save); dialog.Content = panel;
        _launcher?.Close();
        _native.SuspendHotkeyCallbacks = true;
        try { dialog.ShowDialog(); }
        finally { _native.SuspendHotkeyCallbacks = false; }
    }
}
