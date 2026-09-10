[CmdletBinding()]
param([switch]$DesktopFixtures, [switch]$SkipRestore, [string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ResultsDirectory) { $ResultsDirectory = 'artifacts/p5/runs/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8) }
$sdk = $env:CONTINUITYBRIDGE_DOTNET
if (-not $sdk) { $sdk = Join-Path $checkout '.tools/dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$oldPackages = $env:NUGET_PACKAGES
$oldDesktop = $env:CB_P5_DESKTOP_FIXTURES
$cache = Join-Path $checkout '.tools/dotnet-cli-home/.nuget/packages'
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath $cache)) { $env:NUGET_PACKAGES = $cache }
Push-Location -LiteralPath $checkout
try {
    if (-not $SkipRestore) {
        & $sdk restore ContinuityBridge.Windows.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    }
    & $sdk test tests/ContinuityBridge.Core.Tests/ContinuityBridge.Core.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=core.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & $sdk test tests/ContinuityBridge.Windows.Tests/ContinuityBridge.Windows.Tests.csproj -c Release --no-restore --filter 'TestCategory=P5' --logger 'trx;LogFileName=windows-http-image.trx' --results-directory $ResultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Windows image/HTTP tests failed.' }
    if ($DesktopFixtures) {
        Write-Output 'VISIBLE TEST WINDOW: overwrites the clipboard with synthetic fixtures for at most 3 minutes. No private backup. Use the Stop button to cancel. Leaves a synthetic sentinel.'
        if (-not [Environment]::UserInteractive -or (Get-Process -Id $PID).SessionId -eq 0) { throw 'Interactive desktop required.' }
        $env:CB_P5_DESKTOP_FIXTURES = '1'
        & $sdk test tests/ContinuityBridge.Windows.Tests/ContinuityBridge.Windows.Tests.csproj -c Release --no-restore --no-build --filter 'TestCategory=P5Desktop' --logger 'trx;LogFileName=desktop.trx' --results-directory $ResultsDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Real desktop tests failed.' }
    }
    else { Write-Output 'NOT RUN: real clipboard/target windows. Enable only within an announced synthetic test window.' }
}
finally { Pop-Location; $env:NUGET_PACKAGES = $oldPackages; $env:CB_P5_DESKTOP_FIXTURES = $oldDesktop }
