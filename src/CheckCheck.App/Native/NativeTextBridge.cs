using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CheckCheck.App.Native;

public sealed class CaptureSnapshot
{
    public string Text { get; }
    public string DisplayName { get; }
    public string ScopeLabel { get; }
    public bool CanApply => Target is not null;
    internal EditTarget? Target { get; }

    internal CaptureSnapshot(string text, string displayName, string scopeLabel, EditTarget? target = null)
        => (Text, DisplayName, ScopeLabel, Target) = (text, displayName, scopeLabel, target);
}

public sealed record OperationResult(bool Success, string Message);

internal sealed record EditTarget(nint Window, nint Input, uint ProcessId, int[] RuntimeId, string Original,
    int SelectionStart, int SelectionEnd, int ReplaceStart, int ReplaceEnd);

/// <summary>
/// Global capture is opt-in through a hotkey. Direct writes are restricted to Win32's
/// ordinary Edit control; richer editors remain copy-only until an adapter is verified.
/// All UI Automation calls run off the WPF UI thread. Clipboard access runs on its STA.
/// </summary>
public sealed class NativeTextBridge : IDisposable
{
    public const int MaximumTextLength = 10_000;
    private const int HotkeyId = 0x4343;
    private int _activeHotkeyId = HotkeyId;
    private const int WmHotkey = 0x0312;
    private readonly Dispatcher _dispatcher;
    private HwndSource? _source;
    private Func<Task>? _callback;
    private uint _hotkeyModifiers, _hotkeyKey = 0x20;
    private bool _hotkeyDispatching;
    private nint _pendingTarget;
    private bool _disposed;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public NativeTextBridge() => _dispatcher = System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;

    public bool SuspendHotkeyCallbacks { get; set; }
    public event Action<Exception>? HotkeyFailed;

    public void RegisterHotKey(Window window, Action callback, uint modifiers = 3, uint key = 0x20)
        => RegisterHotKeyAsync(window, () => { callback(); return Task.CompletedTask; }, modifiers, key);

    public void RegisterHotKeyAsync(Window window, Func<Task> callback, uint modifiers = 3, uint key = 0x20)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        if (modifiers is 0 or > 15 || key is 0 or > 254 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            throw new InvalidOperationException("Ctrl·Alt·Shift·Win 중 하나와 일반 키를 함께 선택해 주세요.");
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("앱 창을 찾을 수 없어요.");
        if (_source?.Handle == handle && modifiers == _hotkeyModifiers && key == _hotkeyKey)
        {
            _callback = callback;
            return;
        }
        // Register a replacement first so a conflict never loses the existing shortcut.
        int nextId = _source is null ? HotkeyId : _activeHotkeyId == HotkeyId ? HotkeyId + 1 : HotkeyId;
        if (!Native.RegisterHotKey(handle, nextId, 0x4000 | modifiers, key))
            throw new InvalidOperationException("이 단축키를 다른 앱이 사용 중이에요. 다른 조합을 선택해 주세요.");
        if (_source is not null) Native.UnregisterHotKey(_source.Handle, _activeHotkeyId);
        if (_source != source)
        {
            _source?.RemoveHook(WindowMessage);
            source.AddHook(WindowMessage);
        }
        _activeHotkeyId = nextId;
        _callback = callback;
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        _source = source;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == _activeHotkeyId)
        {
            handled = true;
            if (_disposed || SuspendHotkeyCallbacks || _hotkeyDispatching) return 0;
            _pendingTarget = Native.GetForegroundWindow();
            _hotkeyDispatching = true;
            // Return from WM_HOTKEY before any await, UIA or WPF activation. Running an
            // async-void callback inside the native hook can re-enter its message loop.
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
            {
                try
                {
                    if (!_disposed && !SuspendHotkeyCallbacks && _callback is { } callback)
                        await callback();
                }
                catch (Exception ex) { HotkeyFailed?.Invoke(ex); }
                finally { _pendingTarget = 0; _hotkeyDispatching = false; }
            }));
        }
        return 0;
    }

    public async Task<CaptureSnapshot?> CaptureAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = Interlocked.Exchange(ref _pendingTarget, 0);
        if (target == 0) target = Native.GetForegroundWindow();
        return await CaptureTargetAsync(target, selectedOnly: false, restoreTarget: false);
    }

    /// <summary>Call only in response to the user clicking the selection popup.</summary>
    public Task<CaptureSnapshot?> CaptureFromWindowAsync(nint target, bool selectedOnly = true)
        => CaptureTargetAsync(target, selectedOnly, restoreTarget: true);

    private async Task<CaptureSnapshot?> CaptureTargetAsync(nint target, bool selectedOnly, bool restoreTarget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationGate.WaitAsync(0))
            throw new InvalidOperationException("이전 가져오기 작업이 끝난 뒤 다시 눌러 주세요.");
        try
        {
            await WaitForModifiersAsync(_hotkeyKey);
            if (restoreTarget)
            {
                Native.GetWindowThreadProcessId(target, out var pid);
                if (target == 0 || !Native.IsWindow(target) || pid == Environment.ProcessId)
                    throw new InvalidOperationException("글을 선택했던 창이 닫혔어요. 원래 앱에서 다시 선택해 주세요.");
                if (Native.GetForegroundWindow() != target && !Native.SetForegroundWindow(target))
                    throw new InvalidOperationException("원래 앱을 앞으로 가져오지 못했어요. 원래 앱에서 단축키를 눌러 주세요.");
                await Task.Delay(100);
                EnsureExternalWindow(target);
                if (Native.HasOpenMenu(target))
                {
                    Native.DismissMenu();
                    await Task.Delay(100);
                    EnsureExternalWindow(target);
                }
            }
            var attempt = await Task.Run(() => CaptureCore(target, selectedOnly)).WaitAsync(TimeSpan.FromSeconds(4));
            if (attempt.Snapshot is not null) return attempt.Snapshot;
            if (attempt.NoSelection) return null;
            return await CaptureWithCopyAsync(attempt);
        }
        catch (ElementNotAvailableException)
        {
            throw new InvalidOperationException("원래 입력칸이 닫혔거나 바뀌었어요. 글을 다시 선택한 뒤 단축키를 눌러 주세요.");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("원래 앱의 응답이 늦어요. 직접 복사해서 붙여넣어 주세요.");
        }
        catch (COMException)
        {
            throw new InvalidOperationException("원래 앱에서 선택한 글을 읽지 못했어요. 글을 다시 선택하거나 직접 복사해서 붙여넣어 주세요.");
        }
        finally { _operationGate.Release(); }
    }

    internal async Task<CaptureSnapshot?> TryCaptureSelectedAsync(nint window)
    {
        // Right-click previews never issue Ctrl+C or read a whole field as a fallback.
        if (_disposed || !await _operationGate.WaitAsync(0)) return null;
        try { return (await Task.Run(() => CaptureCore(window, selectedOnly: true)).WaitAsync(TimeSpan.FromSeconds(2))).Snapshot; }
        catch { return null; }
        finally { _operationGate.Release(); }
    }

    private static CaptureAttempt CaptureCore(nint window, bool selectedOnly = false)
    {
        EnsureExternalWindow(window);
        var focusedHandle = FocusedHandle(window);
        if (focusedHandle != 0 && string.Equals(Native.ClassName(focusedHandle), "Edit", StringComparison.OrdinalIgnoreCase) &&
            (Native.GetWindowLongPtr(focusedHandle, -16).ToInt64() & 0x20) != 0)
            throw new InvalidOperationException("비밀번호 입력칸은 가져오지 않아요.");
        var element = AutomationElement.FocusedElement
            ?? throw new InvalidOperationException("입력 위치를 찾지 못했어요. 글을 선택해 복사한 뒤 직접 붙여넣어 주세요.");
        ValidateElement(window, element);
        var displayName = WindowName(window);

        // Standard EDIT has an exact UTF-16 range and an undo-aware replacement API.
        if (focusedHandle != 0 && string.Equals(Native.ClassName(focusedHandle), "Edit", StringComparison.OrdinalIgnoreCase))
        {
            var style = Native.GetWindowLongPtr(focusedHandle, -16).ToInt64();
            if ((style & 0x20) != 0) throw new InvalidOperationException("비밀번호 입력칸은 가져오지 않아요.");
            var original = Native.ReadEditText(focusedHandle);
            var (start, end) = Native.ReadSelection(focusedHandle);
            if (start < 0 || end < start || end > original.Length)
                throw new InvalidOperationException("선택 영역을 정확히 확인하지 못했어요. 직접 붙여넣어 주세요.");
            var hasSelection = end > start;
            if (selectedOnly && !hasSelection) return new(window, element.GetRuntimeId(), displayName, null, NoSelection: true);
            // Number-only and forced-case edits may transform supplied text; keep those copy-only.
            if ((style & (0x800 | 0x08 | 0x10 | 0x2000)) == 0 && Native.IsWindowEnabled(focusedHandle))
            {
                var text = hasSelection ? original[start..end] : original;
                CheckText(text);
                Native.GetWindowThreadProcessId(window, out var processId);
                var edit = new EditTarget(window, focusedHandle, processId, element.GetRuntimeId(), original,
                    start, end, hasSelection ? start : 0, hasSelection ? end : original.Length);
                return new(window, element.GetRuntimeId(), displayName,
                    new CaptureSnapshot(text, displayName, hasSelection ? "선택한 영역" : "현재 입력칸 전체", edit));
            }
            if (hasSelection)
            {
                var text = original[start..end];
                CheckText(text);
                return new(window, element.GetRuntimeId(), displayName,
                    new CaptureSnapshot(text, displayName, "선택한 영역 · 복사로 사용"));
            }
        }

        // Chromium and document editors often expose the selection on the enclosing
        // Document rather than on the focused leaf. Read selection only, never all text.
        var selectionNode = element;
        for (var depth = 0; depth < 64 && selectionNode is not null; depth++)
        {
            if (selectionNode.TryGetCurrentPattern(TextPattern.Pattern, out var textObject))
            {
                var pattern = (TextPattern)textObject;
                var ranges = pattern.GetSelection();
                var selected = ranges.Where(r => !string.IsNullOrEmpty(r.GetText(1))).ToArray();
                if (selected.Length > 1)
                    throw new InvalidOperationException("떨어져 있는 여러 영역은 한 번에 가져올 수 없어요. 한 영역만 선택해 주세요.");
                if (selected.Length == 1)
                {
                    var text = selected[0].GetText(MaximumTextLength + 1);
                    CheckText(text);
                    return new(window, element.GetRuntimeId(), displayName,
                        new CaptureSnapshot(text, displayName, "선택한 영역 · 복사로 사용"));
                }
            }
            if ((nint)selectionNode.Current.NativeWindowHandle == window) break;
            selectionNode = TreeWalker.RawViewWalker.GetParent(selectionNode);
        }

        // A writable ValuePattern on an Edit is the only generic whole-field fallback.
        // In particular, a browser Document is never interpreted as the whole page to copy.
        if (!selectedOnly && element.Current.ControlType == ControlType.Edit &&
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
            valueObject is ValuePattern value && !value.Current.IsReadOnly)
        {
            var text = value.Current.Value;
            CheckText(text);
            return new(window, element.GetRuntimeId(), displayName,
                new CaptureSnapshot(text, displayName, "현재 입력칸 전체 · 복사로 사용"));
        }

        return new(window, element.GetRuntimeId(), displayName, null, element.Current.ProcessId);
    }

    private async Task<CaptureSnapshot> CaptureWithCopyAsync(CaptureAttempt attempt)
    {
        var backup = await BackupClipboardAsync();
        uint copiedSequence = 0;
        try
        {
            await Task.Run(() => ValidateFocus(attempt)).WaitAsync(TimeSpan.FromSeconds(4));
            if (Native.GetClipboardSequenceNumber() != backup.Sequence)
                throw new InvalidOperationException("클립보드가 변경되어 가져오기를 멈췄어요. 다시 시도해 주세요.");
            if (ModifiersDown()) throw new InvalidOperationException("단축키에서 손을 뗀 뒤 다시 시도해 주세요.");
            Native.CopySelection();
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(25);
                var sequence = Native.GetClipboardSequenceNumber();
                if (sequence == backup.Sequence) continue;
                var owner = Native.GetClipboardOwner();
                Native.GetWindowThreadProcessId(owner, out var ownerPid);
                Native.GetWindowThreadProcessId(attempt.Window, out var targetPid);
                if (!IsExpectedClipboardOwner(owner, ownerPid, targetPid, attempt.FocusedProcessId))
                    throw new InvalidOperationException("다른 앱에서 클립보드를 변경했어요. 글을 직접 붙여넣어 주세요.");
                copiedSequence = sequence;
                var text = await ReadCopiedTextAsync(copiedSequence);
                await Task.Run(() => ValidateFocus(attempt)).WaitAsync(TimeSpan.FromSeconds(4));
                CheckText(text);
                return new CaptureSnapshot(text, attempt.DisplayName, "복사한 선택 영역 · 복사로 사용");
            }
            throw new InvalidOperationException("선택한 글을 가져오지 못했어요. 글을 선택해 복사한 뒤 직접 붙여넣어 주세요.");
        }
        finally
        {
            if (copiedSequence != 0)
                await _dispatcher.InvokeAsync(() =>
                {
                    // Never overwrite a clipboard that the user or another app changed.
                    if (Native.GetClipboardSequenceNumber() != copiedSequence) return;
                    try
                    {
                        if (backup.Data is null) System.Windows.Clipboard.Clear();
                        else System.Windows.Clipboard.SetDataObject(backup.Data, true);
                    }
                    catch (ExternalException) { /* Busy clipboard: do not retry over a newer copy. */ }
                });
        }
    }

    public async Task<OperationResult> ApplyAsync(CaptureSnapshot snapshot, string replacement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Length > MaximumTextLength)
            return new(false, "한 번에 10,000자까지 반영할 수 있어요.");
        if (replacement.Contains('\0')) return new(false, "지원하지 않는 문자가 포함되어 있어요. 결과 복사를 이용해 주세요.");
        if (snapshot.Target is not { } target)
            return new(false, "이 앱은 결과 복사를 이용해 주세요. 직접 반영은 일반 텍스트 입력칸에서 지원해요.");
        if (!await _operationGate.WaitAsync(0)) return new(false, "이전 작업이 끝난 뒤 다시 시도해 주세요.");
        try
        {
            await WaitForModifiersAsync(_hotkeyKey);
            // Check contents before activating the original app, then repeat after focus settles.
            await Task.Run(() => ValidateEdit(target, requireForeground: false));
            if (!Native.SetForegroundWindow(target.Window))
                return new(false, "원래 앱을 앞으로 가져오지 못했어요. 결과 복사를 이용해 주세요.");
            await Task.Delay(100);
            return await Task.Run(() => ApplyCore(target, replacement));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or Win32Exception)
        {
            return new(false, ex.Message);
        }
        finally { _operationGate.Release(); }
    }

    private static OperationResult ApplyCore(EditTarget target, string replacement)
    {
        ValidateEdit(target, requireForeground: true);
        var expected = target.Original[..target.ReplaceStart] + replacement + target.Original[target.ReplaceEnd..];
        if (expected.Length > MaximumTextLength)
            return new(false, "반영 후 입력칸이 10,000자를 넘어요. 결과 복사를 이용해 주세요.");
        if (expected.Length > Native.ReadTextLimit(target.Input))
            return new(false, "수정한 글이 원래 입력칸의 글자 수 제한을 넘어요. 결과 복사를 이용해 주세요.");
        Native.Select(target.Input, target.ReplaceStart, target.ReplaceEnd);
        // Selection is the only modification so far. Recheck immediately before text mutation.
        if (Native.GetForegroundWindow() != target.Window || FocusedHandle(target.Window) != target.Input ||
            Native.ReadEditText(target.Input) != target.Original ||
            Native.ReadSelection(target.Input) != (target.ReplaceStart, target.ReplaceEnd))
            return new(false, "입력 위치나 원문이 바뀌어 반영을 멈췄어요. 다시 가져와 주세요.");
        Native.ReplaceSelection(target.Input, replacement);
        var actual = Native.ReadEditText(target.Input);
        return actual == expected
            ? new(true, "원래 입력칸에 반영했어요. 원래 앱에서 Ctrl + Z로 되돌릴 수 있어요.")
            : new(false, "앱의 반영 결과가 예상과 달라요. 원래 글을 확인하고 필요하면 원래 앱에서 Ctrl + Z로 되돌려 주세요.");
    }

    private static void ValidateEdit(EditTarget target, bool requireForeground)
    {
        if (!Native.IsWindow(target.Window) || !Native.IsWindow(target.Input) ||
            Native.GetAncestor(target.Input, 2) != target.Window ||
            !string.Equals(Native.ClassName(target.Input), "Edit", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("원래 입력칸이 닫혔거나 바뀌었어요. 다시 가져와 주세요.");
        Native.GetWindowThreadProcessId(target.Input, out var currentPid);
        var style = Native.GetWindowLongPtr(target.Input, -16).ToInt64();
        if (currentPid != target.ProcessId || (style & (0x20 | 0x800 | 0x08 | 0x10 | 0x2000)) != 0 || !Native.IsWindowEnabled(target.Input))
            throw new InvalidOperationException("원래 입력칸에 안전하게 반영할 수 없어요. 결과 복사를 이용해 주세요.");
        if (Native.ReadEditText(target.Input) != target.Original ||
            Native.ReadSelection(target.Input) != (target.SelectionStart, target.SelectionEnd))
            throw new InvalidOperationException("검토하는 동안 원문이나 선택 영역이 바뀌었어요. 글을 다시 가져와 주세요.");
        if (requireForeground)
        {
            if (Native.GetForegroundWindow() != target.Window || FocusedHandle(target.Window) != target.Input)
                throw new InvalidOperationException("입력 위치가 달라져 반영을 멈췄어요. 글을 다시 가져와 주세요.");
            var focused = AutomationElement.FocusedElement;
            if (focused is null) throw new InvalidOperationException("입력 위치를 확인하지 못했어요.");
            if (!focused.GetRuntimeId().SequenceEqual(target.RuntimeId))
                throw new InvalidOperationException("입력칸이 달라져 반영을 멈췄어요. 글을 다시 가져와 주세요.");
            ValidateElement(target.Window, focused);
        }
    }

    private static void ValidateFocus(CaptureAttempt attempt)
    {
        EnsureExternalWindow(attempt.Window);
        var current = AutomationElement.FocusedElement;
        if (current is null || !current.GetRuntimeId().SequenceEqual(attempt.RuntimeId))
            throw new InvalidOperationException("입력 위치가 바뀌어 가져오기를 멈췄어요. 다시 시도해 주세요.");
        ValidateElement(attempt.Window, current);
    }

    private static void ValidateElement(nint window, AutomationElement element)
    {
        // Browser accessibility providers may run in a renderer process. The UIA
        // ancestry must still reach the exact foreground HWND; PID equality alone
        // incorrectly rejects otherwise valid selected text in those browsers.
        var node = element;
        for (var i = 0; i < 64 && node is not null; i++)
        {
            if (node.Current.IsPassword) throw new InvalidOperationException("비밀번호 입력칸은 가져오지 않아요.");
            if ((nint)node.Current.NativeWindowHandle == window) return;
            node = TreeWalker.RawViewWalker.GetParent(node);
        }
        throw new InvalidOperationException("입력칸의 원래 창을 확인하지 못했어요. 직접 붙여넣어 주세요.");
    }

    private static void EnsureExternalWindow(nint window)
    {
        Native.GetWindowThreadProcessId(window, out var pid);
        if (window == 0 || !Native.IsWindow(window) || Native.GetForegroundWindow() != window)
            throw new InvalidOperationException("원래 앱이 바뀌었어요. 글을 쓰던 앱에서 단축키를 눌러 주세요.");
        if (pid == Environment.ProcessId)
            throw new InvalidOperationException("검사할 글이 있는 다른 앱에서 설정한 전역 단축키를 눌러 주세요.");
    }

    private static nint FocusedHandle(nint window)
    {
        var thread = Native.GetWindowThreadProcessId(window, out _);
        var info = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        return Native.GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    private static string WindowName(nint window)
    {
        var title = new StringBuilder(256);
        Native.GetWindowText(window, title, title.Capacity);
        return title.Length > 0 ? title.ToString() : "원래 앱";
    }

    private static void CheckText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("검사할 글이 없어요. 글을 선택한 뒤 다시 눌러 주세요.");
        if (text.Length > MaximumTextLength) throw new InvalidOperationException("한 번에 10,000자까지 검사할 수 있어요. 일부를 선택해 주세요.");
    }

    private static bool ModifiersDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => Native.GetAsyncKeyState(k) < 0);

    private static async Task WaitForModifiersAsync(uint triggerKey)
    {
        for (var i = 0; i < 80; i++)
        {
            if (!ModifiersDown() && Native.GetAsyncKeyState((int)triggerKey) >= 0) return;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("단축키에서 손을 뗀 뒤 다시 시도해 주세요.");
    }

    internal static bool IsExpectedClipboardOwner(nint owner, uint ownerPid, uint targetPid, int focusedPid)
        // OpenClipboard(NULL) is legal and has no owner HWND. The foreground window
        // and exact UIA focused element are revalidated before and after this read.
        => owner == 0 || ownerPid == targetPid || focusedPid > 0 && ownerPid == (uint)focusedPid;

    private async Task<ClipboardBackup> BackupClipboardAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await _dispatcher.InvokeAsync(BackupClipboard); }
            catch (ExternalException) when (attempt < 7) { await Task.Delay(25); }
            catch (ExternalException) { throw new InvalidOperationException("다른 앱이 클립보드를 사용 중이에요. 잠시 후 단축키를 다시 눌러 주세요."); }
        }
    }

    private async Task<string> ReadCopiedTextAsync(uint expectedSequence)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _dispatcher.InvokeAsync(() =>
                {
                    if (Native.GetClipboardSequenceNumber() != expectedSequence)
                        throw new InvalidOperationException("클립보드가 변경되어 가져오기를 멈췄어요.");
                    if (!System.Windows.Clipboard.ContainsText())
                        throw new InvalidOperationException("선택한 글을 찾지 못했어요. 글을 블록으로 선택한 뒤 다시 눌러 주세요.");
                    var text = System.Windows.Clipboard.GetText();
                    if (Native.GetClipboardSequenceNumber() != expectedSequence)
                        throw new InvalidOperationException("클립보드가 변경되어 가져오기를 멈췄어요.");
                    return text;
                });
            }
            catch (ExternalException) when (attempt < 7) { await Task.Delay(25); }
            catch (ExternalException) { throw new InvalidOperationException("원래 앱의 복사가 아직 끝나지 않았어요. 잠시 후 다시 눌러 주세요."); }
        }
    }

    private static ClipboardBackup BackupClipboard()
    {
        var sequence = Native.GetClipboardSequenceNumber();
        var source = System.Windows.Clipboard.GetDataObject();
        if (source is null) return new(sequence, null);
        var clone = new System.Windows.DataObject();
        foreach (var format in source.GetFormats(autoConvert: false))
        {
            var value = source.GetData(format, autoConvert: false);
            object? copy = value switch
            {
                null => null,
                string s => s,
                string[] strings => strings.ToArray(),
                byte[] bytes => bytes.ToArray(),
                MemoryStream stream => new MemoryStream(stream.ToArray()),
                BitmapSource bitmap => bitmap.Clone(),
                System.Drawing.Image bitmap => bitmap.Clone(),
                _ => throw new InvalidOperationException("현재 클립보드를 그대로 보존하기 어려워요. 글을 직접 복사해서 붙여넣어 주세요.")
            };
            if (copy is not null) clone.SetData(format, copy, autoConvert: false);
        }
        if (Native.GetClipboardSequenceNumber() != sequence)
            throw new InvalidOperationException("클립보드가 변경되었어요. 다시 시도해 주세요.");
        return new(sequence, clone);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_source is not null)
        {
            Native.UnregisterHotKey(_source.Handle, _activeHotkeyId);
            _source.RemoveHook(WindowMessage);
            _source = null;
        }
        _callback = null;
    }

    private sealed record CaptureAttempt(nint Window, int[] RuntimeId, string DisplayName, CaptureSnapshot? Snapshot,
        int FocusedProcessId = 0, bool NoSelection = false);
    private sealed record ClipboardBackup(uint Sequence, System.Windows.DataObject? Data);

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hWnd, int id);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
        [DllImport("user32.dll")] internal static extern bool IsWindow(nint hWnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hWnd);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hWnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLong64(nint hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint hWnd, int index);
        internal static nint GetWindowLongPtr(nint hWnd, int index)
            => nint.Size == 8 ? GetWindowLong64(hWnd, index) : GetWindowLong32(hWnd, index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hWnd, StringBuilder text, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder text, int capacity);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
        [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern nint SendTextMessageTimeout(nint window, uint message, nint wParam, StringBuilder text, uint flags, uint timeout, out nint result);

        internal static string ClassName(nint input)
        {
            var text = new StringBuilder(128);
            GetClassName(input, text, text.Capacity);
            return text.ToString();
        }

        internal static string ReadEditText(nint input)
        {
            var length = Message(input, 0x000E, 0, 0).ToInt64();
            if (length < 0 || length > MaximumTextLength)
                throw new InvalidOperationException("입력칸이 너무 길어요. 일부를 복사해서 직접 붙여넣어 주세요.");
            var text = new StringBuilder((int)length + 1);
            if (SendTextMessageTimeout(input, 0x000D, text.Capacity, text, 0x0003, 700, out _) == 0)
                throw new InvalidOperationException("원래 입력칸이 응답하지 않아요. 잠시 후 다시 시도해 주세요.");
            // Detect a concurrent edit instead of accepting a potentially truncated snapshot.
            if (Message(input, 0x000E, 0, 0).ToInt64() != text.Length)
                throw new InvalidOperationException("가져오는 동안 글이 바뀌었어요. 다시 시도해 주세요.");
            return text.ToString();
        }

        internal static (int Start, int End) ReadSelection(nint input)
        {
            var start = Marshal.AllocHGlobal(sizeof(int));
            var end = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(start, -1);
                Marshal.WriteInt32(end, -1);
                Message(input, 0x00B0, start, end);
                return (Marshal.ReadInt32(start), Marshal.ReadInt32(end));
            }
            finally { Marshal.FreeHGlobal(start); Marshal.FreeHGlobal(end); }
        }

        internal static void Select(nint input, int start, int end) => Message(input, 0x00B1, start, end);

        internal static long ReadTextLimit(nint input) => Message(input, 0x00D5, 0, 0).ToInt64();

        internal static void ReplaceSelection(nint input, string replacement)
        {
            // EM_REPLACESEL accepts only plain text and records the edit in native Undo.
            var memory = Marshal.StringToHGlobalUni(replacement);
            try { Message(input, 0x00C2, 1, memory); }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static nint Message(nint window, uint message, nint wParam, nint lParam)
        {
            if (SendMessageTimeout(window, message, wParam, lParam, 0x0003, 700, out var result) == 0)
                throw new InvalidOperationException("원래 입력칸이 응답하지 않거나 접근이 제한되어 있어요. 결과 복사를 이용해 주세요.");
            return result;
        }

        internal static void CopySelection()
        {
            var events = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true) };
            var sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
            if (sent == events.Length) return;
            if (sent > 0) SendInput(1, new[] { Key(0x11, true) }, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("이 앱에서 선택한 글을 복사할 수 없어요. 직접 복사해서 붙여넣어 주세요.");
        }

        internal static bool HasOpenMenu(nint window)
        {
            var thread = GetWindowThreadProcessId(window, out _);
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref info) && (info.Flags & 0x1C) != 0;
        }

        internal static void DismissMenu()
        {
            var events = new[] { Key(0x1B), Key(0x1B, true) };
            if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) == events.Length) return;
            throw new InvalidOperationException("원래 앱의 메뉴를 닫지 못했어요. Esc로 메뉴를 닫고 설정한 단축키를 눌러 주세요.");
        }

        private static Input Key(ushort key, bool up = false) => new()
        {
            Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } }
        };

        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
        {
            public uint Size, Flags;
            public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public int Left, Top, Right, Bottom;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
        {
            public ushort VirtualKey, Scan;
            public uint Flags, Time;
            public nuint ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public nuint ExtraInfo;
        }
    }
}
