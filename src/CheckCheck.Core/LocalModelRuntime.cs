using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace CheckCheck.Core;

/// <summary>Owns a private, loopback-only llama.cpp process and verified local model files.</summary>
public sealed class LocalModelRuntime : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient downloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient localClient = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private Uri? serverUri;
    private bool modelVerified;
    private volatile bool ready;
    private bool disposed;
    public bool IsInstalled => File.Exists(ModelCatalog.ModelPath) && new FileInfo(ModelCatalog.ModelPath).Length == ModelCatalog.ModelSize &&
        (FindServer(ModelCatalog.RuntimeDirectory(false)) != null || FindServer(ModelCatalog.RuntimeDirectory(true)) != null);
    public string DeviceDisplayName { get; private set; } = "내 PC";
    public bool IsReady
    {
        get { try { return ready && process is { HasExited: false }; } catch (InvalidOperationException) { return false; } }
    }

    public LocalModelRuntime() => downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("CheckCheck/0.2 (+https://github.com/youkdonghun/checkcheck)");

    public async Task EnsureReadyAsync(IProgress<EngineProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var ct = linked.Token;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady) return;
            if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
                throw new PlatformNotSupportedException("로컬 교정은 Windows 10/11 64비트 PC를 지원해요.");
            Directory.CreateDirectory(ModelCatalog.CacheRoot);
            // A file lock serializes downloads between app instances without a global installation.
            await using var installLock = await AcquireInstallLockAsync(ct).ConfigureAwait(false);
            if (!modelVerified)
            {
                await EnsureDownloadAsync(ModelCatalog.ModelUrl, ModelCatalog.ModelPath, ModelCatalog.ModelSha256, ModelCatalog.ModelSize, "교정 모델", progress, ct).ConfigureAwait(false);
                modelVerified = true;
            }
            var preferCpu = File.Exists(Path.Combine(ModelCatalog.CacheRoot, "Runtime", "prefer-cpu"));
            if (!preferCpu)
            {
                try
                {
                    var executable = await EnsureRuntimeAsync(false, progress, ct).ConfigureAwait(false);
                    await StartAsync(executable, false, progress, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    StopProcess();
                    progress?.Report(new("그래픽 가속을 사용할 수 없어 CPU 모드로 준비하고 있어요."));
                }
            }
            var cpuExecutable = await EnsureRuntimeAsync(true, progress, ct).ConfigureAwait(false);
            await StartAsync(cpuExecutable, true, progress, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(ModelCatalog.CacheRoot, "Runtime", "prefer-cpu"), ModelCatalog.RuntimeVersion, ct).ConfigureAwait(false);
        }
        catch { StopProcess(); throw; }
        finally { gate.Release(); }
    }

    public async Task<string> CompleteAsync(string instructions, string text, int maximumTokens, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (serverUri == null || process == null || process.HasExited) throw new InvalidOperationException("교정 엔진을 먼저 준비해 주세요.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        var payload = new
        {
            model = "checkcheck-local",
            messages = new[] { new { role = "system", content = instructions }, new { role = "user", content = JsonSerializer.Serialize(new { original = text }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) } },
            temperature = 0.2, top_p = 0.8, top_k = 20, min_p = 0.0, repeat_penalty = 1.05, seed = 42,
            // The fixed proofreading instructions remain in the private server's RAM; never on disk.
            // Reusing that prefix avoids re-evaluating hundreds of tokens for every selection.
            max_tokens = maximumTokens, stream = false, cache_prompt = true,
            chat_template_kwargs = new { enable_thinking = false },
            response_format = new
            {
                type = "json_object",
                schema = new { type = "object", properties = new { revised = new { type = "string" } }, required = new[] { "revised" }, additionalProperties = false }
            }
        };
        try
        {
            using var response = await localClient.PostAsJsonAsync(new Uri(serverUri, "v1/chat/completions"), payload, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"로컬 교정 엔진 응답 오류 ({(int)response.StatusCode}). 앱을 다시 열어 재시도해 주세요.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
            var choice = json.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
                throw new InvalidOperationException("교정 결과가 길이 제한에 도달했어요. 더 짧은 문단으로 나누어 검사해 주세요.");
            var content = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            using var result = JsonDocument.Parse(content);
            return result.RootElement.GetProperty("revised").GetString() ?? throw new InvalidOperationException("교정문이 비어 있어요. 다시 검사해 주세요.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("로컬 교정에 4분 이상 걸리고 있어요. 짧은 문단으로 나누어 다시 검사해 주세요."); }
    }

    private async Task<FileStream> AcquireInstallLockAsync(CancellationToken ct)
    {
        var path = Path.Combine(ModelCatalog.CacheRoot, ".install.lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromMinutes(20)) { await Task.Delay(700, ct).ConfigureAwait(false); }
        }
    }

    private async Task<string> EnsureRuntimeAsync(bool cpu, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        var directory = ModelCatalog.RuntimeDirectory(cpu);
        var executable = FindServer(directory);
        if (executable != null && File.Exists(Path.Combine(directory, ".verified"))) return executable;
        var archive = directory + ".zip";
        await EnsureDownloadAsync(cpu ? ModelCatalog.CpuUrl : ModelCatalog.VulkanUrl, archive, cpu ? ModelCatalog.CpuSha256 : ModelCatalog.VulkanSha256,
            cpu ? ModelCatalog.CpuSize : ModelCatalog.VulkanSize, cpu ? "CPU 실행 도구" : "그래픽 실행 도구", progress, ct).ConfigureAwait(false);
        progress?.Report(new("교정 실행 도구를 설치하고 있어요."));
        // Extract into a unique sibling; the final folder is never considered installed midway.
        var staging = directory + ".extract-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':'))
                    throw new InvalidDataException("실행 도구 압축 파일의 경로가 올바르지 않아요.");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
            if (FindServer(staging) == null) throw new InvalidDataException("실행 도구에서 llama-server.exe를 찾지 못했어요.");
            await File.WriteAllTextAsync(Path.Combine(staging, ".verified"), cpu ? ModelCatalog.CpuSha256 : ModelCatalog.VulkanSha256, ct).ConfigureAwait(false);
            // These paths derive only from our fixed catalog under the application's cache root.
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            Directory.Move(staging, directory);
            return FindServer(directory)!;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static string? FindServer(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    private async Task EnsureDownloadAsync(string url, string path, string sha256, long expectedSize, string label, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            progress?.Report(new($"{label} 파일을 확인하고 있어요."));
            if (await VerifyFileAsync(path, sha256, expectedSize, ct).ConfigureAwait(false)) return;
            File.Delete(path);
        }
        var partial = path + ".partial";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > expectedSize) { File.Delete(partial); offset = 0; }
                if (offset < expectedSize)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode != HttpStatusCode.PartialContent) offset = 0;
                    else if (response.Content.Headers.ContentRange?.From != offset) throw new InvalidDataException("다운로드 이어받기 범위가 올바르지 않아요.");
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                    var buffer = new byte[1024 * 1024];
                    var lastReport = Stopwatch.StartNew();
                    int count;
                    while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        offset += count;
                        if (offset > expectedSize) throw new InvalidDataException("다운로드 크기가 예상 값과 달라요.");
                        if (lastReport.ElapsedMilliseconds >= 150)
                        {
                            progress?.Report(new($"{label} 내려받는 중 · {offset / 1048576:N0} / {expectedSize / 1048576:N0} MB", (double)offset / expectedSize));
                            lastReport.Restart();
                        }
                    }
                }
                progress?.Report(new($"{label} 무결성을 확인하고 있어요."));
                if (!await VerifyFileAsync(partial, sha256, expectedSize, ct).ConfigureAwait(false))
                { File.Delete(partial); throw new InvalidDataException("다운로드 파일 검증에 실패했어요."); }
                File.Move(partial, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                progress?.Report(new($"{label} 다운로드를 다시 시도하고 있어요 ({attempt + 1}/3)."));
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> VerifyFileAsync(string path, string hash, long size, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var actual = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(actual).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private async Task StartAsync(string executable, bool cpu, IProgress<EngineProgress>? progress, CancellationToken ct)
    {
        StopProcess();
        var device = cpu ? null : await FindPreferredDeviceAsync(executable, ct).ConfigureAwait(false);
        progress?.Report(new(cpu ? "CPU에서 교정 모델을 불러오고 있어요." : "내 PC에서 교정 모델을 불러오고 있어요."));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardOutput = true, RedirectStandardError = true };
        // Drop inherited llama configuration so environment variables cannot enable tools or remote models.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        foreach (var arg in new[] { "--model", ModelCatalog.ModelPath, "--alias", "checkcheck-local", "--host", "127.0.0.1", "--port", port.ToString(),
            "--ctx-size", "8192", "--parallel", "1", "--threads", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(),
            "--batch-size", cpu ? "256" : "512", "--ubatch-size", cpu ? "128" : "256", "--n-gpu-layers", cpu ? "0" : "99", "--flash-attn", "on", "--cache-type-k", "q8_0", "--cache-type-v", "q8_0",
            "--jinja", "--chat-template-kwargs", "{\"enable_thinking\":false}", "--reasoning", "off", "--no-webui", "--no-slots", "--cache-prompt", "--no-agent", "--offline", "--log-disable" }) info.ArgumentList.Add(arg);
        if (device != null) { info.ArgumentList.Add("--device"); info.ArgumentList.Add(device.Value.Id); }
        info.Environment["LLAMA_API_KEY"] = token;
        process = Process.Start(info) ?? throw new InvalidOperationException("로컬 엔진을 실행하지 못했어요.");
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        serverUri = new Uri($"http://127.0.0.1:{port}/");
        localClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMinutes(2))
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"로컬 교정 엔진을 시작하지 못했어요 (코드 {process.ExitCode}).");
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(2));
                using var response = await localClient.GetAsync(new Uri(serverUri, "health"), probe.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    // A Vulkan executable alone does not prove GPU acceleration is available.
                    DeviceDisplayName = cpu ? "CPU" : device == null ? "로컬 엔진 · 장치 자동 선택" : device.Value.Name;
                    ready = true;
                    progress?.Report(new("로컬 교정 준비 완료", 1)); return;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("교정 모델을 불러오는 데 시간이 오래 걸려요. 다른 프로그램을 닫고 다시 시도해 주세요.");
    }

    private static async Task<(string Id, string Name)?> FindPreferredDeviceAsync(string executable, CancellationToken ct)
    {
        // Vulkan device 0 can be integrated graphics on hybrid laptops. Prefer a discrete GPU
        // with enough free memory for this 4B model, rather than spreading it onto shared memory.
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.ArgumentList.Add("--list-devices");
        using var probe = Process.Start(info);
        if (probe == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var stdout = probe.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = probe.StandardError.ReadToEndAsync(timeout.Token);
            await probe.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var devices = ParseDevices(await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false));
            return devices.Count == 0 ? null : devices[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { if (!probe.HasExited) try { probe.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    internal static IReadOnlyList<(string Id, string Name)> ParseDevices(string text)
    {
        return Regex.Matches(text, @"(?m)^\s*(Vulkan\d+):\s*(.+?)\s+\((\d+) MiB,\s*(\d+) MiB free\)")
            .Select(m => new { Id = m.Groups[1].Value, Name = m.Groups[2].Value, Free = long.Parse(m.Groups[4].Value) })
            .OrderByDescending(d => d.Free >= ModelCatalog.ModelSize / 1048576 + 600 &&
                Regex.IsMatch(d.Name, @"NVIDIA|Radeon\s+(?:RX|PRO)|(?:Intel.*)?Arc", RegexOptions.IgnoreCase))
            .ThenByDescending(d => d.Free)
            .Select(d => (d.Id, d.Name)).ToArray();
    }

    private void StopProcess()
    {
        ready = false;
        var owned = Interlocked.Exchange(ref process, null);
        serverUri = null;
        if (owned == null) return;
        try { if (!owned.HasExited) { owned.Kill(entireProcessTree: true); owned.WaitForExit(3000); } } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        finally { owned.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); StopProcess(); downloadClient.Dispose(); localClient.Dispose();
        // Active async work may still observe the semaphore and cancellation source.
    }
}
