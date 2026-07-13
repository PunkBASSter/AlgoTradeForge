<#
.SYNOPSIS
    Seeds the default Binance collection groups into a running HistoryLoader via the API.

.DESCRIPTION
    Collection is configured declaratively as GROUPS (durable, SQLite-backed, API/UI-editable) —
    not the legacy appsettings.json asset list. This script PUTs the canonical default Binance
    group set (docs/binance-default-groups.json) into a running service. Idempotent: re-running
    overwrites each group with the same definition.

    Use it to bootstrap a fresh install (empty groups), or to reset the Binance defaults.

.PARAMETER BaseUrl
    HistoryLoader API base. Default: http://localhost:5210

.PARAMETER GroupsFile
    Path to the groups JSON array. Default: docs/binance-default-groups.json (repo-relative).

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/seed-binance-groups.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5210',
    [string]$GroupsFile
)

$ErrorActionPreference = 'Stop'

if (-not $GroupsFile) {
    $repoRoot  = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    $GroupsFile = Join-Path $repoRoot 'docs/binance-default-groups.json'
}
if (-not (Test-Path $GroupsFile)) { Write-Error "Groups file not found: $GroupsFile"; exit 1 }

$groups = Get-Content $GroupsFile -Raw | ConvertFrom-Json
Write-Host "[seed] $($groups.Count) groups from $GroupsFile -> $BaseUrl" -ForegroundColor Cyan

foreach ($g in $groups) {
    $name = $g.name
    $body = $g | ConvertTo-Json -Depth 10 -Compress
    try {
        $r = Invoke-WebRequest -Uri "$BaseUrl/api/v1/groups/$name" -Method Put `
            -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 15
        Write-Host ("  PUT {0,-34} HTTP {1}" -f $name, $r.StatusCode) -ForegroundColor Green
    } catch {
        $resp = $_.Exception.Response
        $code = if ($resp) { [int]$resp.StatusCode } else { '???' }
        Write-Host ("  PUT {0,-34} FAILED HTTP {1}: {2}" -f $name, $code, $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host "[seed] done. Verify: curl $BaseUrl/api/v1/groups" -ForegroundColor Cyan
