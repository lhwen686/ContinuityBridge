[CmdletBinding()]
param(
    [ValidateSet('setup', 'status', 'test-core')]
    [string]$Action = 'setup'
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This entry point requires Windows.'
}
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location -LiteralPath $repoRoot
try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is missing; install separately.' }
    $gitRoot = & git rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'No Git checkout found. No files were changed.' }
    if ([IO.Path]::GetFullPath($gitRoot) -ne [IO.Path]::GetFullPath($repoRoot)) {
        throw 'The script directory is not the expected Git root.'
    }
    if ($Action -eq 'status') {
        & git status --short --branch
        if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
        $revision = & git rev-parse --verify --quiet HEAD
        if ($LASTEXITCODE -eq 0) { Write-Output "Commit: $revision" }
        else { Write-Output 'NOT RUN: No committed revision; worktree handoff needs an initial commit.' }
        exit 0
    }
    $localDotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($env:CONTINUITYBRIDGE_DOTNET) { $dotnet = $env:CONTINUITYBRIDGE_DOTNET }
    elseif (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $dotnet = $localDotnet }
    elseif ($dotnetCommand) { $dotnet = $dotnetCommand.Source }
    else { throw 'BLOCKED: SDK missing. Provision global.json SDK separately; no installation attempted.' }
    $sdkOutput = & $dotnet --version 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'BLOCKED: Selected SDK cannot satisfy global.json.' }
    $sdkVersion = [version]($sdkOutput | Select-Object -Last 1)
    $requested = [version]((Get-Content -LiteralPath global.json -Raw | ConvertFrom-Json).sdk.version)
    if ($sdkVersion.Major -ne $requested.Major -or $sdkVersion.Minor -ne $requested.Minor -or
        [math]::Floor($sdkVersion.Build / 100) -ne [math]::Floor($requested.Build / 100) -or
        $sdkVersion -lt $requested) { throw 'BLOCKED: SDK does not match the current latestPatch policy.' }
    Write-Output "PASS: Windows checkout and SDK $sdkVersion available. No dependencies installed."
    if ($Action -eq 'test-core') {
        $project = 'tests/ContinuityBridge.Core.Tests/ContinuityBridge.Core.Tests.csproj'
        if (-not (Test-Path -LiteralPath 'tests/ContinuityBridge.Core.Tests/obj/project.assets.json')) {
            throw 'BLOCKED: Restore assets missing. Review dotnet restore --locked-mode separately.'
        }
        & $dotnet test $project --no-restore --logger 'console;verbosity=minimal'
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
        Write-Output 'PASS: Core test command completed. Inspect counts/skips; this is not a device or G2 result.'
    }
    else { Write-Output 'NOT RUN: build, tests, Win32 clipboard, tray, autostart and remote/device operations.' }
}
finally { Pop-Location }
