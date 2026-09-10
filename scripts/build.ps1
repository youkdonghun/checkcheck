param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExe = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:LOCALAPPDATA 'CheckCheck\BuildTools\dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExe)) { throw '.NET 10 SDK가 필요합니다: https://dotnet.microsoft.com/download/dotnet/10.0' }
if (-not $SkipTests) {
    & $dotnetExe run --project (Join-Path $projectRoot 'tests\CheckCheck.Tests\CheckCheck.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
}
$releasePath = Join-Path $projectRoot 'dist'
& $dotnetExe publish (Join-Path $projectRoot 'src\CheckCheck.App\CheckCheck.App.csproj') -c Release -r win-x64 --self-contained true -o $releasePath -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\사용안내.txt') -Destination (Join-Path $releasePath '사용안내.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $releasePath 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\licenses') -Destination $releasePath -Recurse -Force
$packageFiles = @((Join-Path $releasePath '체크체크.exe'), (Join-Path $releasePath '사용안내.txt'), (Join-Path $releasePath 'THIRD-PARTY-NOTICES.md'), (Join-Path $releasePath 'licenses'))
Compress-Archive -LiteralPath $packageFiles -DestinationPath (Join-Path $releasePath '체크체크-win-x64.zip') -Force
Copy-Item -LiteralPath (Join-Path $releasePath '체크체크.exe') -Destination (Join-Path $releasePath 'CheckCheck.exe') -Force
Copy-Item -LiteralPath (Join-Path $releasePath '체크체크-win-x64.zip') -Destination (Join-Path $releasePath 'CheckCheck-win-x64.zip') -Force
$checksumLines = foreach ($assetName in @('CheckCheck.exe', 'CheckCheck-win-x64.zip')) {
    $assetHash = (Get-FileHash -LiteralPath (Join-Path $releasePath $assetName) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$assetHash  $assetName"
}
[IO.File]::WriteAllLines((Join-Path $releasePath 'SHA256SUMS.txt'), $checksumLines, [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath (Join-Path $releasePath '체크체크.exe'), (Join-Path $releasePath '체크체크-win-x64.zip') | Select-Object Name,Length,FullName
