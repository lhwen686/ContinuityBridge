[CmdletBinding()]
param([switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sdk = $env:CONTINUITYBRIDGE_DOTNET
if (-not $sdk) {
    $sdk = Join-Path $checkout '.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
}
$originalPackages = $env:NUGET_PACKAGES
$existingCache = Join-Path $checkout '.tools/dotnet-cli-home/.nuget/packages'
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath $existingCache)) { $env:NUGET_PACKAGES = $existingCache }
Push-Location $checkout
try {
    if (-not $SkipRestore) {
        & $sdk restore ContinuityBridge.Portable.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Portable locked restore failed.' }
    }
    & $sdk test tests/ContinuityBridge.Relay.Tests/ContinuityBridge.Relay.Tests.csproj -c Release --no-restore --logger 'trx;LogFileName=relay.trx' --results-directory artifacts/relay-test-results
    if ($LASTEXITCODE -ne 0) { throw 'Relay state/process tests failed.' }
    $python = (Get-Command python -ErrorAction Stop).Source
    & $python tests/relay-blackbox/run.py --launch $sdk (Join-Path $checkout 'src/ContinuityBridge.Relay/bin/Release/net10.0/ContinuityBridge.Relay.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Relay black-box tests failed.' }
}
finally { Pop-Location; $env:NUGET_PACKAGES = $originalPackages }
