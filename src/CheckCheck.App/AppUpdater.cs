using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckCheck.Core;

namespace CheckCheck.App;

internal sealed record AppRelease(Version Version, string Tag, Uri Executable, long Size, Uri Checksums);

internal static class AppUpdater
{
    internal const string ReleasesUrl = "https://github.com/youkdonghun/checkcheck/releases/latest";
    internal static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 2, 0);
    private const string AssetPrefix = "https://github.com/youkdonghun/checkcheck/releases/download/";
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient() { var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; client.DefaultRequestHeaders.UserAgent.ParseAdd("CheckCheck/0.2"); return client; }

    internal static async Task<AppRelease?> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Client.GetAsync("https://api.github.com/repos/youkdonghun/checkcheck/releases/latest", timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    internal static AppRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version)) return null;
        var current = CurrentVersion;
        if (version <= new Version(current.Major, current.Minor, current.Build)) return null;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var exe = assets.Single(a => a.GetProperty("name").GetString() == "CheckCheck.exe");
        var sums = assets.Single(a => a.GetProperty("name").GetString() == "SHA256SUMS.txt");
        var exeUrl = ValidateAsset(exe.GetProperty("browser_download_url").GetString()!, tag, "CheckCheck.exe");
        var sumUrl = ValidateAsset(sums.GetProperty("browser_download_url").GetString()!, tag, "SHA256SUMS.txt");
        long size = exe.GetProperty("size").GetInt64();
        if (size < 1_000_000 || size > 400_000_000) throw new InvalidDataException("업데이트 파일 크기가 올바르지 않아요.");
        return new(version, tag, exeUrl, size, sumUrl);
    }

    private static Uri ValidateAsset(string url, string tag, string name)
    {
        if (url != AssetPrefix + tag + "/" + name) throw new InvalidDataException("공식 체크체크 릴리스 파일이 아니에요.");
        return new Uri(url);
    }

    internal static string ReadHash(string sums)
    {
        var matches = Regex.Matches(sums, @"(?m)^([a-fA-F0-9]{64})[ \t]+\*?CheckCheck\.exe\r?$");
        if (matches.Count != 1) throw new InvalidDataException("업데이트 파일의 검증 정보를 찾지 못했어요.");
        return matches[0].Groups[1].Value;
    }

    internal static async Task<(string Path, string Hash)> DownloadAsync(AppRelease release, IProgress<EngineProgress> progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(10)); token = timeout.Token;
        var sums = await Client.GetStringAsync(release.Checksums, token);
        var hash = ReadHash(sums);
        string folder = Path.Combine(ModelCatalog.CacheRoot, "Updates", release.Tag); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "CheckCheck.exe"), partial = path + ".partial";
        using var response = await Client.GetAsync(release.Executable, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("업데이트 파일 크기가 배포 정보와 달라요.");
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            {
                byte[] buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read; if (total > release.Size) throw new InvalidDataException("업데이트 파일이 예상 크기를 넘었어요.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress.Report(new($"새 버전 내려받는 중 · {total / 1048576} / {release.Size / 1048576} MB", (double)total / release.Size));
                }
                if (total != release.Size) throw new InvalidDataException("업데이트 파일 다운로드가 끝나지 않았어요.");
            }
            await using (var file = File.OpenRead(partial))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("업데이트 파일 검증에 실패했어요. 기존 앱은 유지됩니다.");
            }
            var info = FileVersionInfo.GetVersionInfo(partial);
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) != release.Version) throw new InvalidDataException("업데이트 실행 파일의 버전이 배포 정보와 달라요.");
            File.Move(partial, path, true); return (path, hash);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static void LaunchInstaller(string stagedPath, string hash)
    {
        string target = Environment.ProcessPath ?? throw new IOException("현재 실행 파일을 찾지 못했어요.");
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("배포용 EXE에서 업데이트를 실행해 주세요.");
        // Test write access before quitting the running app; no elevation or protected-directory writes.
        string probe = target + ".write-check-" + Guid.NewGuid().ToString("N");
        using (File.Create(probe)) { } File.Delete(probe);
        string folder = Path.GetDirectoryName(stagedPath)!;
        string script = Path.Combine(folder, "install.ps1"), config = Path.Combine(folder, "install.json");
        File.WriteAllText(script, InstallScript, new UTF8Encoding(true));
        File.WriteAllText(config, JsonSerializer.Serialize(new { Parent = Environment.ProcessId, Target = target, Source = stagedPath, Hash = hash }), new UTF8Encoding(true));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Config", config }) info.ArgumentList.Add(arg);
        using var installer = Process.Start(info) ?? throw new IOException("업데이트 도우미를 실행하지 못했어요.");
    }

    internal const string InstallScript = """
        param([Parameter(Mandatory=$true)][string]$Config, [switch]$Headless)
        $ErrorActionPreference = 'Stop'
        # Module search paths can be inherited from PowerShell 7 or another host.
        # Hash directly with .NET so the Windows PowerShell helper needs no hashing module.
        function Get-UpdateHash([string]$Path) {
            $stream = [IO.File]::OpenRead($Path)
            $sha = [Security.Cryptography.SHA256]::Create()
            try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose(); $stream.Dispose() }
        }
        $update = Get-Content -LiteralPath $Config -Raw -Encoding UTF8 | ConvertFrom-Json
        $target = [IO.Path]::GetFullPath($update.Target)
        $source = [IO.Path]::GetFullPath($update.Source)
        $backup = $target + '.previous'
        $candidate = $target + '.updating'
        $moved = $false
        try {
            if (-not $target.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) -or $target -eq $source) { throw 'Invalid update target.' }
            if ((Get-UpdateHash $source) -ne $update.Hash) { throw 'Update checksum mismatch.' }
            $parentProcess = Get-Process -Id $update.Parent -ErrorAction SilentlyContinue
            if ($parentProcess -and -not $parentProcess.WaitForExit(30000)) { throw 'CheckCheck did not exit in time.' }
            Copy-Item -LiteralPath $source -Destination $candidate -Force
            if ((Get-UpdateHash $candidate) -ne $update.Hash) { throw 'Copied update checksum mismatch.' }
            Move-Item -LiteralPath $target -Destination $backup -Force
            $moved = $true
            Move-Item -LiteralPath $candidate -Destination $target -Force
            $restart = Start-Process -FilePath $target -WorkingDirectory ([IO.Path]::GetDirectoryName($target)) -WindowStyle Normal -PassThru
            if (-not $restart) { throw 'Could not restart CheckCheck.' }
        } catch {
            $failure = $_.Exception.Message
            if ($moved -and (Test-Path -LiteralPath $backup)) {
                Copy-Item -LiteralPath $backup -Destination $target -Force
                Start-Process -FilePath $target -WorkingDirectory ([IO.Path]::GetDirectoryName($target)) -WindowStyle Normal
            }
            [IO.File]::WriteAllText($Config + '.error', $failure, [Text.Encoding]::UTF8)
            if (-not $Headless) {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.MessageBox]::Show("업데이트를 완료하지 못했습니다. 기존 실행 파일을 다시 실행해 주세요.`n" + $failure, '체크체크 업데이트') | Out-Null
            }
            exit 1
        }
        """;
}
