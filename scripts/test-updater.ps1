$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $workspace '.cache\qa\updater-fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$oldCode = 'using System; using System.IO; using System.Reflection; using System.Threading; class Probe { static void Main() { File.WriteAllText(Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".ran"), "old"); Thread.Sleep(3500); } }'
$newCode = 'using System; using System.IO; using System.Reflection; class Probe { static void Main() { File.WriteAllText(Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".ran"), "new"); } }'
$target = Join-Path $fixture 'target.exe'
$source = Join-Path $fixture 'source.exe'
[IO.File]::WriteAllText((Join-Path $fixture 'old.cs'), $oldCode)
[IO.File]::WriteAllText((Join-Path $fixture 'new.cs'), $newCode)
& $compiler /nologo /target:winexe "/out:$target" (Join-Path $fixture 'old.cs')
if ($LASTEXITCODE -ne 0) { throw 'Old fixture compilation failed' }
& $compiler /nologo /target:winexe "/out:$source" (Join-Path $fixture 'new.cs')
if ($LASTEXITCODE -ne 0) { throw 'New fixture compilation failed' }
$oldHash = (Get-FileHash -LiteralPath $target).Hash
$hash = (Get-FileHash -LiteralPath $source).Hash
$code = [IO.File]::ReadAllText((Join-Path $workspace 'src\CheckCheck.App\AppUpdater.cs'))
$match = [regex]::Match($code, '(?s)internal const string InstallScript = """\r?\n(.*?)\r?\n        """;')
if (-not $match.Success) { throw 'Installer script source missing' }
$script = Join-Path $fixture 'install.ps1'
[IO.File]::WriteAllText($script, $match.Groups[1].Value, [Text.UTF8Encoding]::new($true))
$errors = $null
[Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) { throw ($errors | Out-String) }
$old = Start-Process -FilePath $target -WindowStyle Hidden -PassThru
$config = Join-Path $fixture 'install.json'
@{ Parent = $old.Id; Target = $target; Source = $source; Hash = $hash } | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding UTF8
$run = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $script + '"'), '-Config', ('"' + $config + '"'), '-Headless') -WindowStyle Hidden -PassThru
if (-not $run.WaitForExit(45000)) { Stop-Process -Id $run.Id -Force; throw 'Installer fixture timed out after 45 seconds.' }
if ($run.ExitCode -ne 0) {
    $failure = if (Test-Path -LiteralPath ($config + '.error')) { [IO.File]::ReadAllText($config + '.error') } else { 'No error detail was recorded.' }
    throw "Installer failed ($($run.ExitCode)): $failure"
}
for ($i = 0; $i -lt 30; $i++) { if ((Get-Content -LiteralPath (Join-Path $fixture 'target.ran') -ErrorAction SilentlyContinue) -eq 'new') { break }; Start-Sleep -Milliseconds 100 }
if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw 'Installed file mismatch' }
if ((Get-FileHash -LiteralPath ($target + '.previous')).Hash -ne $oldHash) { throw 'Rollback file mismatch' }
if ((Get-Content -LiteralPath (Join-Path $fixture 'target.ran')) -ne 'new') { throw 'Updated application did not restart' }
$badConfig = Join-Path $fixture 'invalid-hash.json'
@{ Parent = $PID; Target = $target; Source = $source; Hash = ('0' * 64) } | ConvertTo-Json | Set-Content -LiteralPath $badConfig -Encoding UTF8
$rejected = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $script + '"'), '-Config', ('"' + $badConfig + '"'), '-Headless') -WindowStyle Hidden -PassThru
if (-not $rejected.WaitForExit(15000)) { Stop-Process -Id $rejected.Id -Force; throw 'Invalid-hash fixture timed out.' }
if ($rejected.ExitCode -ne 1 -or [IO.File]::ReadAllText($badConfig + '.error') -notmatch 'Update checksum mismatch') { throw 'Invalid update hash was not rejected clearly.' }
if ((Get-FileHash -LiteralPath $target).Hash -ne $hash) { throw 'Rejected update changed the installed application.' }
'PASS: updater script syntax, parent exit wait, .NET checksum, file replacement, previous-version backup, restart, invalid-hash rejection without mutation' | Tee-Object -FilePath (Join-Path $workspace '.cache\qa\updater-result.txt')
