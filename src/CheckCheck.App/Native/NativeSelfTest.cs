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

            const string original = "앞 문장. 오류 문장. 뒷 문장.";
            var offset = original.IndexOf("오류", StringComparison.Ordinal);
            Probe.Text(plain, original);
            Probe.Message(plain, 0x00B1, offset, offset + 2);
            await FocusAsync(fixtureWindow, PlainId);
            var selection = await bridge.CaptureAsync() ?? throw new InvalidOperationException("선택 영역 캡처가 비어 있어요.");
            Assert(selection.Text == "오류" && selection.CanApply, "selected native text capture", checks);
            var popupSelection = await bridge.TryCaptureSelectedAsync(fixtureWindow);
            Assert(popupSelection?.Text == "오류", "right-click capture reads selected text only", checks);
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateWindowEx(
            uint exStyle, string className, string title, uint style, int x, int y, int width,
            int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        private static extern nint SendText(nint window, uint message, nint wParam, StringBuilder text, uint flags, uint timeout, out nint result);

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
