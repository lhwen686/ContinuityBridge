[CmdletBinding()]
param()

# Offline contract checks only. No service, clipboard, restore or device access.
$ErrorActionPreference = 'Stop'
try {
    if (-not (Get-Command Test-Json -ErrorAction SilentlyContinue)) {
        throw 'BLOCKED: Test-Json is unavailable; use an already installed compatible PowerShell. Do not install tools for P0.'
    }
    $schema = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'schemas.json') -Raw | ConvertFrom-Json -AsHashtable
    $cases = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'schema-cases.json') -Raw | ConvertFrom-Json -AsHashtable
    $api = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'openapi.json') -Raw | ConvertFrom-Json -AsHashtable
    $ids = [System.Collections.Generic.HashSet[string]]::new()
    $passed = 0
    foreach ($case in $cases) {
        if (-not $ids.Add($case.id)) { throw "Duplicate case ID: $($case.id)" }
        if (-not $schema['$defs'].ContainsKey($case.schema)) { throw "Unknown schema: $($case.schema)" }
        $wrapper = @{
            '$schema' = $schema['$schema']
            '$defs' = $schema['$defs']
            '$ref' = '#/$defs/' + $case.schema
        } | ConvertTo-Json -Depth 100 -Compress
        $json = ConvertTo-Json -InputObject $case.value -Depth 100 -Compress
        $validationErrors = @()
        $actual = Test-Json -Json $json -Schema $wrapper -ErrorAction SilentlyContinue -ErrorVariable validationErrors
        foreach ($errorRecord in $validationErrors) {
            if ($errorRecord.FullyQualifiedErrorId -notlike 'InvalidJsonAgainstSchema*') {
                throw "Schema engine failure in $($case.id): $($errorRecord.FullyQualifiedErrorId)"
            }
        }
        if ($actual -ne $case.valid) { throw "Case $($case.id): expected $($case.valid), got $actual" }
        $passed++
    }

    # Resolve every local JSON Pointer rather than checking only that files exist.
    function Test-References($node, $document) {
        if ($node -is [System.Collections.IDictionary]) {
            if ($node.Contains('$ref')) {
                $reference = [string]$node['$ref']
                if ($reference.StartsWith('./schemas.json#')) {
                    $target = $schema
                    $pointer = $reference.Substring('./schemas.json#'.Length)
                }
                elseif ($reference.StartsWith('#/')) {
                    $target = $document
                    $pointer = $reference.Substring(1)
                }
                else { throw "Nonlocal or unsupported reference: $reference" }
                foreach ($part in $pointer.Substring(1).Split('/')) {
                    $key = $part.Replace('~1', '/').Replace('~0', '~')
                    if ($target -isnot [System.Collections.IDictionary] -or -not $target.Contains($key)) {
                        throw "Unresolved reference: $reference"
                    }
                    $target = $target[$key]
                }
            }
            foreach ($value in $node.Values) { Test-References $value $document }
        }
        elseif ($node -is [array]) { foreach ($value in $node) { Test-References $value $document } }
    }
    Test-References $schema $schema
    Test-References $api $api

    $operationIds = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($path in $api.paths.Keys) {
        foreach ($method in $api.paths[$path].Keys) {
            $operation = $api.paths[$path][$method]
            if (-not $operationIds.Add($operation.operationId)) { throw 'Duplicate operationId' }
            if ($path.StartsWith('/v1/') -and $operation.Contains('security')) { throw 'Unexpected v1 security override' }
            if ($method -in @('post', 'delete')) {
                $refs = @($operation.parameters | ForEach-Object { $_['$ref'] })
                foreach ($name in @('IfMatch', 'IdempotencyKey')) {
                    if ('#/components/parameters/' + $name -notin $refs) { throw "Missing mutation header: $path $name" }
                }
            }
        }
    }
    if ($operationIds.Count -ne 9 -or $api.security.Count -ne 1 -or
        -not $api.security[0].Contains('deviceBearer') -or
        $api.security[0].deviceBearer.Count -ne 0) { throw 'Unexpected endpoint/security inventory' }
    $defaults = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'default-capabilities.json') -Raw
    $capWrapper = @{ '$schema' = $schema['$schema']; '$defs' = $schema['$defs']; '$ref' = '#/$defs/Capabilities' } | ConvertTo-Json -Depth 100
    if (-not (Test-Json -Json $defaults -Schema $capWrapper)) { throw 'Default capabilities are invalid' }
    Write-Output "PASS: $passed positive/negative schema cases; all local references; 9 HTTP operations and mutation header inventory; default capabilities."
    Write-Output 'NOT RUN: full OpenAPI meta-schema certification, runtime byte/image/CAS/TTL/auth enforcement, service tests, Windows clipboard, iPhone and deployment.'
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
