[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$sdk = $env:CONTINUITYBRIDGE_DOTNET
if (-not $sdk) { $sdk = Join-Path $checkout '.tools/dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$oldPackages = $env:NUGET_PACKAGES
$cache = Join-Path $checkout '.tools/dotnet-cli-home/.nuget/packages'
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath $cache)) { $env:NUGET_PACKAGES = $cache }
Push-Location -LiteralPath $checkout
try {
    if (@(git status --porcelain).Count -ne 0) { throw 'Package only a clean committed candidate.' }
    $sha = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw 'Candidate SHA unavailable.' }
    $destination = Join-Path $checkout ('artifacts/p5/packages/' + $sha)
    if (Test-Path -LiteralPath $destination) { throw 'Candidate output already exists; preserve it and choose a new candidate after changes.' }
    New-Item -ItemType Directory -Path $destination | Out-Null
    $entries = @(
        @{Name='ContinuityBridge-Windows'; Project='src/ContinuityBridge.App/ContinuityBridge.App.csproj'; Exe='ContinuityBridge.App.exe'},
        @{Name='ContinuityBridge-TestAgent-staging'; Project='src/ContinuityBridge.TestAgent/ContinuityBridge.TestAgent.csproj'; Exe='ContinuityBridge.TestAgent.exe'}
    )
    $packages = @()
    foreach ($entry in $entries) {
        $raw = Join-Path $destination ($entry.Name + '-publish')
        $package = Join-Path $destination $entry.Name
        & $sdk publish $entry.Project -c Release -r win-x64 --no-restore -o $raw "-p:SourceRevisionId=$sha" -p:IncludeNativeLibrariesForSelfExtract=true
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        New-Item -ItemType Directory -Path $package | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $raw -File) {
            if ($file.Extension -notin @('.exe', '.dll', '.json')) { continue }
            if ($entry.Name -eq 'ContinuityBridge-Windows' -and $file.Name -match '(?i)qa|test|fixture|secret|credential') { throw 'QA/secret file in product publish.' }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $package $file.Name)
        }
        if ($entry.Name -eq 'ContinuityBridge-Windows') {
            Copy-Item -LiteralPath docs/cloud-clipboard-v1/windows/USER_GUIDE.md -Destination (Join-Path $package '使用说明.md')
        } else {
            Copy-Item -LiteralPath docs/cloud-clipboard-v1/windows/QA_RUNBOOK.md -Destination (Join-Path $package '测试使用说明.md')
            Copy-Item -LiteralPath docs/cloud-clipboard-v1/windows/fixtures.json -Destination (Join-Path $package 'fixtures.json')
        }
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $package $entry.Exe)
        $files = @(Get-ChildItem -LiteralPath $package -File | ForEach-Object {
            @{path=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        })
        @{sourceSha=$sha; cleanSource=$true; platform='win-x64'; protocol=1; signatureStatus=[string]$signature.Status; files=$files} |
            ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding utf8NoBOM
        $zip = Join-Path $destination ($entry.Name + '-' + $sha.Substring(0,12) + '.zip')
        Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
        $packages += @{name=[IO.Path]::GetFileName($zip); sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(); signature=[string]$signature.Status}
    }
    @{sourceSha=$sha; packages=$packages; productionDeployed=$false; iphoneE2E='NOT RUN'} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $destination 'release.json') -Encoding utf8NoBOM
    Write-Output "Candidate packages created: $sha"
    $packages | ConvertTo-Json -Depth 5
}
finally { Pop-Location; $env:NUGET_PACKAGES = $oldPackages }
