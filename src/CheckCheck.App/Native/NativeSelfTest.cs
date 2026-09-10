using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace CheckCheck.App.Native;

/// <summary>Opt-in integration checks that only read/write our disposable fixture process.</summary>
public static class NativeSelfTest
{
    private const int PlainId = 1101;
    private const int ReadOnlyId = 1102;
    private const int PasswordId = 1103;
    private const uint FocusMessage = 0x8022;

    public static void RunFixture()
    {
        Forms.Application.EnableVisualStyles();
        using var form = new FixtureForm();
        Forms.Application.Run(form);
    }

    internal static async Task<IReadOnlyList<string>> RunHotkeyChecksAsync(System.Windows.Window testWindow)
    {
        var checks = new List<string>();
        Assert(NativeTextBridge.IsExpectedClipboardOwner(0, 0, 11, 12), "ownerless clipboard is accepted", checks);
        Assert(NativeTextBridge.IsExpectedClipboardOwner(100, 11, 11, 12), "target clipboard owner is accepted", checks);
        Assert(NativeTextBridge.IsExpectedClipboardOwner(100, 12, 11, 12), "browser renderer clipboard owner is accepted", checks);
        Assert(!NativeTextBridge.IsExpectedClipboardOwner(100, 13, 11, 12), "unrelated clipboard owner is rejected", checks);
        using var bridge = new NativeTextBridge();
        var handle = new System.Windows.Interop.WindowInteropHelper(testWindow).EnsureHandle();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Func<Task> callback = () => { calls++; completed.TrySetResult(); return Task.CompletedTask; };
        bridge.RegisterHotKeyAsync(testWindow, callback, 3, 0x86);
        bridge.RegisterHotKeyAsync(testWindow, callback, 3, 0x86);
        checks.Add("registering the same custom shortcut preserves its registration");
        Probe.Message(handle, 0x0312, 0x4343, 0);
        Assert(!completed.Task.IsCompleted, "hotkey callback is deferred until native message returns", checks);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(calls == 1, "deferred hotkey callback completes", checks);
        await Task.Delay(30);
        bridge.SuspendHotkeyCallbacks = true;
        Probe.Message(handle, 0x0312, 0x4343, 0);
        await Task.Delay(30);
        Assert(calls == 1, "shortcut recording suspension does not trigger capture", checks);
        bridge.SuspendHotkeyCallbacks = false;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        bridge.RegisterHotKeyAsync(testWindow, async () => { calls++; started.TrySetResult(); await release.Task; finished.TrySetResult(); }, 3, 0x86);
        Probe.Message(handle, 0x0312, 0x4343, 0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Probe.Message(handle, 0x0312, 0x4343, 0);
        await Task.Delay(30);
        Assert(calls == 1, "repeated hotkey does not overlap a pending capture", checks);
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);

        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.HotkeyFailed += ex => failed.TrySetResult(ex);
        bridge.RegisterHotKeyAsync(testWindow, () => Task.FromException(new InvalidOperationException("fixture callback error")), 3, 0x86);
        Probe.Message(handle, 0x0312, 0x4343, 0);
        var caught = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(caught.Message == "fixture callback error", "async hotkey error is contained and reported", checks);
        return checks;
    }

    public static async Task RunAsync(string outputPath)
    {
        var checks = new List<string>();
        var originalForeground = Probe.GetForegroundWindow();
        var clipboardSequence = Probe.GetClipboardSequenceNumber();
        Process? fixture = null;
        nint fixtureWindow = 0;
        Exception? failure = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("테스트 실행 파일을 찾지 못했어요.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                    ?? throw new InvalidOperationException("앱 어셈블리를 찾지 못했어요.");
                var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
                if (!File.Exists(assemblyPath)) throw new InvalidOperationException("앱 어셈블리 파일을 찾지 못했어요.");
                start.ArgumentList.Add(assemblyPath);
            }
            start.ArgumentList.Add("--native-fixture");
            fixture = Process.Start(start) ?? throw new InvalidOperationException("테스트 입력 창을 시작하지 못했어요.");
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(50);
                fixture.Refresh();
                if (fixture.HasExited) throw new InvalidOperationException("테스트 입력 창이 예상보다 일찍 닫혔어요.");
                fixtureWindow = fixture.MainWindowHandle;
                if (fixtureWindow != 0 && Probe.GetDlgItem(fixtureWindow, PlainId) != 0) break;
            }
            if (fixtureWindow == 0) throw new InvalidOperationException("테스트 입력 창이 준비되지 않았어요.");
            Probe.GetWindowThreadProcessId(fixtureWindow, out var windowProcess);
            Assert(windowProcess == fixture.Id, "fixture process isolation", checks);
            var plain = Probe.GetDlgItem(fixtureWindow, PlainId);
            var readOnly = Probe.GetDlgItem(fixtureWindow, ReadOnlyId);
            var password = Probe.GetDlgItem(fixtureWindow, PasswordId);
            Assert(plain != 0 && readOnly != 0 && password != 0, "fixture controls available", checks);
            using var bridge = new NativeTextBridge();
            await FocusAsync(fixtureWindow, PlainId);
            await RunMouseChecksAsync(fixtureWindow, plain, bridge, checks);

            const string original = "앞 문장. 오류 문장. 뒷 문장.";
            var offset = original.IndexOf("오류", StringComparison.Ordinal);
            Probe.Text(plain, original);
            Probe.Message(plain, 0x00B1, offset, offset + 2);
            await FocusAsync(fixtureWindow, PlainId);
            var selection = await bridge.CaptureAsync() ?? throw new InvalidOperationException("선택 영역 캡처가 비어 있어요.");
            Assert(selection.Text == "오류" && selection.CanApply, "selected native text capture", checks);
            var popupSelection = await bridge.TryCaptureSelectedAsync(fixtureWindow);
            Assert(popupSelection?.Text == "오류", "right-click capture reads selected text only", checks);
            var explicitSelection = await bridge.CaptureFromWindowAsync(fixtureWindow);
            Assert(explicitSelection?.Text == "오류", "explicit popup command reads only its target selection", checks);
            var applied = await bridge.ApplyAsync(selection, "수정");
            Assert(applied.Success, "selected native replacement", checks);
            Assert(Probe.Read(plain) == original.Replace("오류", "수정", StringComparison.Ordinal), "exact native readback", checks);
            Probe.Message(plain, 0x00C7, 0, 0);
            Assert(Probe.Read(plain) == original, "native undo restores original", checks);

            Probe.Text(plain, original);
            Probe.Message(plain, 0x00B1, offset, offset + 2);
            await FocusAsync(fixtureWindow, PlainId);
            var stale = await bridge.CaptureAsync() ?? throw new InvalidOperationException("캡처가 비어 있어요.");
            const string userChanged = "사용자가 바꾼 원문.";
            Probe.Text(plain, userChanged);
            var rejected = await bridge.ApplyAsync(stale, "수정");
            Assert(!rejected.Success && Probe.Read(plain) == userChanged, "stale original rejected without write", checks);

            Probe.Text(plain, original);
            Probe.Message(plain, 0x00B1, offset, offset + 2);
            await FocusAsync(fixtureWindow, PlainId);
            var movedSelection = await bridge.CaptureAsync() ?? throw new InvalidOperationException("캡처가 비어 있어요.");
            Probe.Message(plain, 0x00B1, 0, 1);
            rejected = await bridge.ApplyAsync(movedSelection, "수정");
            Assert(!rejected.Success && Probe.Read(plain) == original, "changed selection rejected without write", checks);

            Probe.Message(plain, 0x00B1, 3, 3);
            await FocusAsync(fixtureWindow, PlainId);
            var whole = await bridge.CaptureAsync() ?? throw new InvalidOperationException("캡처가 비어 있어요.");
            Assert(await bridge.TryCaptureSelectedAsync(fixtureWindow) is null, "right-click capture never falls back to entire input", checks);
            Assert(await bridge.CaptureFromWindowAsync(fixtureWindow) is null, "explicit selected-only command never copies the entire input", checks);
            Assert(whole.Text == original && whole.CanApply && whole.ScopeLabel == "현재 입력칸 전체", "whole editable field capture", checks);
            applied = await bridge.ApplyAsync(whole, "전체 수정 문장.");
            Assert(applied.Success && Probe.Read(plain) == "전체 수정 문장.", "whole field replacement", checks);

            Probe.Text(plain, "오류");
            Probe.Message(plain, 0x00C5, 4, 0);
            Probe.Message(plain, 0x00B1, 0, 2);
            await FocusAsync(fixtureWindow, PlainId);
            var limited = await bridge.CaptureAsync() ?? throw new InvalidOperationException("캡처가 비어 있어요.");
            rejected = await bridge.ApplyAsync(limited, "가나다라마");
            Assert(!rejected.Success && Probe.Read(plain) == "오류", "input length limit checked before write", checks);

            Probe.Message(readOnly, 0x00B1, 0, 4);
            await FocusAsync(fixtureWindow, ReadOnlyId);
            var locked = await bridge.CaptureAsync() ?? throw new InvalidOperationException("읽기 전용 캡처가 비어 있어요.");
            Assert(!locked.CanApply, "read-only selection is copy-only", checks);
            rejected = await bridge.ApplyAsync(locked, "변경 금지");
            Assert(!rejected.Success && Probe.Read(readOnly) == "읽기전용 테스트", "read-only replacement rejected", checks);

            await FocusAsync(fixtureWindow, PasswordId);
            var passwordRejected = false;
            try { await bridge.CaptureAsync(); }
            catch (InvalidOperationException ex) { passwordRejected = ex.Message.Contains("비밀번호", StringComparison.Ordinal); }
            Assert(passwordRejected, "password field capture rejected", checks);
            Assert(await bridge.TryCaptureSelectedAsync(fixtureWindow) is null, "right-click capture excludes passwords", checks);
            passwordRejected = false;
            try { await bridge.CaptureFromWindowAsync(fixtureWindow); }
            catch (InvalidOperationException ex) { passwordRejected = ex.Message.Contains("비밀번호", StringComparison.Ordinal); }
            Assert(passwordRejected, "explicit popup command excludes passwords", checks);
            Assert(Probe.GetClipboardSequenceNumber() == clipboardSequence, "clipboard untouched by native path", checks);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // Close only the exact child process we started; never enumerate or modify user apps.
            var restoreFocus = fixtureWindow != 0 && Probe.GetForegroundWindow() == fixtureWindow;
            if (fixture is not null)
            {
                try
                {
                    if (!fixture.HasExited)
                    {
                        if (fixtureWindow != 0) Probe.Message(fixtureWindow, 0x0010, 0, 0);
                        try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (TimeoutException) { fixture.Kill(); }
                    }
                }
                finally { fixture.Dispose(); }
            }
            if (restoreFocus && originalForeground != 0) Probe.SetForegroundWindow(originalForeground);
        }
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(new
        {
            success = failure is null,
            timestampUtc = DateTimeOffset.UtcNow,
            checks,
            error = failure is null ? null : failure.GetType().Name + ": " + failure.Message,
            scope = "Only a disposable CheckCheck fixture process. No user text or clipboard data recorded."
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure is not null) throw new InvalidOperationException("전역 입력 자체 테스트가 실패했어요. 결과 파일을 확인해 주세요.", failure);
    }

    private static void Assert(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException("Native self-test failed: " + name);
        checks.Add(name);
    }

    private static async Task RunMouseChecksAsync(nint fixtureWindow, nint plain, NativeTextBridge bridge, List<string> checks)
    {
        if (!Probe.GetCursorPos(out var savedPointer))
            throw new InvalidOperationException("자체 테스트에서 마우스 위치를 읽지 못했어요.");
        // A blank client-area point has no native edit context menu and cannot run
        // commands. All input is gated by both the foreground HWND and hit-test HWND.
        var testPoint = new Probe.Point { X = 10, Y = 225 };
        if (!Probe.ClientToScreen(fixtureWindow, ref testPoint))
            throw new InvalidOperationException("자체 테스트 창의 위치를 읽지 못했어요.");
        var shown = NewLauncherEvent();
        int callbackCount = 0;
        using var watcher = new SelectionPopupWatcher(bridge, System.Windows.Application.Current.Dispatcher,
            (target, selection, x, y) => { callbackCount++; shown.TrySetResult((target, selection, x, y)); }, () => { });
        try
        {
            Probe.Message(plain, 0x00B1, 0, 0);
            Probe.RightClickFixture(fixtureWindow, testPoint);
            var emptyEvent = await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(emptyEvent.Target == fixtureWindow, "actual global right-click reaches fixture launcher callback", checks);
            Assert(emptyEvent.Selection is null, "actual right-click opens launcher even without a selection cache", checks);
            Assert(emptyEvent.X == testPoint.X && emptyEvent.Y == testPoint.Y, "actual right-click supplies pointer position for launcher", checks);
            Assert(Probe.GetForegroundWindow() == fixtureWindow, "watcher does not steal foreground focus", checks);

            const string sample = "앞 오류 뒤";
            Probe.Text(plain, sample);
            Probe.Message(plain, 0x00B1, 2, 4);
            await FocusAsync(fixtureWindow, PlainId);
            shown = NewLauncherEvent();
            Probe.RightClickFixture(fixtureWindow, testPoint);
            var selectedEvent = await shown.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(selectedEvent.Target == fixtureWindow && selectedEvent.Selection is null,
                "actual right-click without preceding drag still offers a capture command", checks);
            var capture = await bridge.CaptureFromWindowAsync(selectedEvent.Target);
            Assert(capture?.Text == "오류", "launcher target command reads selected fixture text after actual right-click", checks);

            watcher.Dispose();
            var callsBeforeDispose = callbackCount;
            Probe.RightClickFixture(fixtureWindow, testPoint);
            await Task.Delay(100);
            Assert(callbackCount == callsBeforeDispose, "disposing the global watcher removes its mouse hook", checks);
        }
        finally
        {
            // Do not move the pointer back if the user moved it or changed apps.
            if (Probe.GetForegroundWindow() == fixtureWindow && Probe.GetCursorPos(out var current) &&
                current.X == testPoint.X && current.Y == testPoint.Y)
                Probe.SetCursorPos(savedPointer.X, savedPointer.Y);
        }
        static TaskCompletionSource<(nint Target, CaptureSnapshot? Selection, int X, int Y)> NewLauncherEvent()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task FocusAsync(nint fixture, int controlId)
    {
        Probe.SetForegroundWindow(fixture);
        Probe.Message(fixture, FocusMessage, controlId, 0);
        await Task.Delay(150);
        if (Probe.GetForegroundWindow() != fixture)
            throw new InvalidOperationException("Windows가 자체 테스트 창의 포커스를 허용하지 않았어요.");
    }

    private sealed class FixtureForm : Forms.Form
    {
        public FixtureForm()
        {
            Text = "체크체크 · 임시 자체 테스트 입력 창";
            ClientSize = new System.Drawing.Size(580, 240);
            StartPosition = Forms.FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            FormBorderStyle = Forms.FormBorderStyle.FixedToolWindow;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            CreateEdit(PlainId, "테스트 원문", 20, 0x0004 | 0x0040 | 0x1000);
            CreateEdit(ReadOnlyId, "읽기전용 테스트", 95, 0x0800);
            CreateEdit(PasswordId, "fixture-only-password", 160, 0x0020);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // The child is launched hidden; only this explicitly requested self-test
            // reveals its own temporary fixture so Windows can give its edit a focus.
            Probe.ShowWindow(Handle, 5);
        }

        private void CreateEdit(int id, string value, int y, uint editStyle)
        {
            var handle = Probe.CreateWindowEx(0x00000200, "EDIT", value,
                0x40000000 | 0x10000000 | 0x00010000 | editStyle,
                20, y, 540, 50, Handle, id, 0, 0);
            if (handle == 0) throw new InvalidOperationException("네이티브 테스트 입력칸을 만들지 못했어요.");
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == FocusMessage)
            {
                var input = Probe.GetDlgItem(Handle, message.WParam.ToInt32());
                if (input != 0) Probe.SetFocus(input);
                message.Result = 1;
                return;
            }
            base.WndProc(ref message);
        }
    }

    private static class Probe
    {
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint SetFocus(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern nint GetDlgItem(nint window, int id);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
        [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] events, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(
            uint exStyle, string className, string title, uint style, int x, int y, int width,
            int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern nint SendText(nint window, uint message, nint wParam, StringBuilder text, uint flags, uint timeout, out nint result);

        internal static void RightClickFixture(nint fixture, Point point)
        {
            if (GetForegroundWindow() != fixture || GetAncestor(WindowFromPoint(point), 2) != fixture)
                throw new InvalidOperationException("자체 테스트 창이 가려지거나 포커스가 바뀌어 마우스 입력을 보내지 않았어요.");
            if (new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12 }.Any(key => GetAsyncKeyState(key) < 0))
                throw new InvalidOperationException("마우스 또는 보조 키가 눌려 있어 자체 테스트 입력을 보내지 않았어요.");
            if (!SetCursorPos(point.X, point.Y) || !GetCursorPos(out var actual) || actual.X != point.X || actual.Y != point.Y ||
                GetForegroundWindow() != fixture || GetAncestor(WindowFromPoint(actual), 2) != fixture)
                throw new InvalidOperationException("자체 테스트 창 안의 마우스 위치를 확인하지 못해 입력을 보내지 않았어요.");
            var events = new[] {
                new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0008 } } },
                new Input { Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0010 } } }
            };
            var sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
            if (sent == events.Length) return;
            if (sent > 0) SendInput(1, new[] { events[1] }, Marshal.SizeOf<Input>());
            throw new InvalidOperationException("Windows가 자체 테스트 창의 마우스 입력을 허용하지 않았어요.");
        }

        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public nuint ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
        {
            public ushort Key, Scan;
            public uint Flags, Time;
            public nuint ExtraInfo;
        }

        internal static nint Message(nint window, uint message, nint wParam, nint lParam)
        {
            if (SendMessageTimeout(window, message, wParam, lParam, 0x0003, 1_000, out var result) == 0)
                throw new InvalidOperationException("자체 테스트 입력 창이 응답하지 않아요.");
            return result;
        }

        internal static void Text(nint input, string text)
        {
            var memory = Marshal.StringToHGlobalUni(text);
            try { Message(input, 0x000C, 0, memory); }
            finally { Marshal.FreeHGlobal(memory); }
        }

        internal static string Read(nint input)
        {
            var value = new StringBuilder(10_001);
            if (SendText(input, 0x000D, value.Capacity, value, 0x0003, 1_000, out _) == 0)
                throw new InvalidOperationException("자체 테스트 글을 읽지 못했어요.");
            return value.ToString();
        }
    }
}
