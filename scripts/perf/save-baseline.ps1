<#
.SYNOPSIS
    Runs the AlgoTradeForge benchmark harness and archives the brief JSON
    results outside the repo so we can diff Mean/Allocated across commits.

.DESCRIPTION
    Drops every *-report-brief.json from BenchmarkDotNet.Artifacts/results into:

        ~/.algo-tradeforge/perf-history/<short-sha>-<utc-stamp>[-<label>]/

    Per-machine, machine-id stamped in metadata.json so cross-machine
    comparisons don't accidentally lie.

.PARAMETER Filter
    BenchmarkDotNet --filter glob. Defaults to '*' (run everything).

.PARAMETER Job
    'default' (full warmup + iterations) or 'dry' (smoke run, single iter).

.PARAMETER Label
    Optional human-readable suffix on the snapshot dir, e.g. 'pre-refactor'.

.EXAMPLE
    powershell.exe -File scripts/perf/save-baseline.ps1
    powershell.exe -File scripts/perf/save-baseline.ps1 -Filter '*Backtest_5y*' -Label 'pre-fix'
    powershell.exe -File scripts/perf/save-baseline.ps1 -Job dry
#>
[CmdletBinding()]
param(
    [string]$Filter = '*',
    [ValidateSet('default', 'dry')]
    [string]$Job = 'default',
    [string]$Label = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repoRoot

# Pre-flight: warn if competing dotnet processes are alive - they will trash
# the measurement signal. We don't kill them; that's the user's call.
$competing = Get-Process -Name dotnet -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }
if ($competing) {
    $pids = ($competing | ForEach-Object { $_.Id }) -join ', '
    Write-Warning "Other dotnet processes are running (PIDs: $pids). Benchmarks are CPU-sensitive - consider stopping them and re-running."
}

$shortSha = (& git rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "git rev-parse failed (not in a git repo?)." }

$dirtyOutput = (& git status --porcelain)
$dirty = ''
if ($dirtyOutput) { $dirty = '-dirty' }

$utcStamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$labelPart = ''
if ($Label) { $labelPart = "-$Label" }
$snapshotName = "$shortSha$dirty-$utcStamp$labelPart"

$historyRoot = Join-Path $HOME '.algo-tradeforge\perf-history'
$snapshotDir = Join-Path $historyRoot $snapshotName
New-Item -ItemType Directory -Path $snapshotDir -Force | Out-Null

Write-Host "-> snapshot dir: $snapshotDir" -ForegroundColor Cyan

$bdnArgs = @('-c', 'Release', '--project', 'benchmarks/AlgoTradeForge.Benchmarks', '--', '--filter', $Filter)
if ($Job -eq 'dry') { $bdnArgs += @('--job', 'dry') }

Write-Host "-> dotnet run $($bdnArgs -join ' ')" -ForegroundColor Cyan
& dotnet run @bdnArgs
if ($LASTEXITCODE -ne 0) { throw "BenchmarkDotNet run failed (exit $LASTEXITCODE)." }

$resultsDir = Join-Path $repoRoot 'BenchmarkDotNet.Artifacts\results'
if (-not (Test-Path $resultsDir)) { throw "Expected results dir missing: $resultsDir" }

$briefFiles = Get-ChildItem -Path $resultsDir -Filter '*-report-brief.json' -File
if ($briefFiles.Count -eq 0) {
    throw "No *-report-brief.json files found. Did the [Config(typeof(BriefJsonConfig))] attribute compile in?"
}
$briefFiles | Copy-Item -Destination $snapshotDir
Get-ChildItem -Path $resultsDir -Filter '*-report-github.md' -File | Copy-Item -Destination $snapshotDir

$meta = [ordered]@{
    sha       = $shortSha
    dirty     = [bool]$dirty
    utc       = $utcStamp
    label     = $Label
    filter    = $Filter
    job       = $Job
    machine   = [Environment]::MachineName
    os        = [Environment]::OSVersion.VersionString
    cpu_count = [Environment]::ProcessorCount
    files     = ($briefFiles | ForEach-Object { $_.Name })
}
$metaPath = Join-Path $snapshotDir 'metadata.json'
$meta | ConvertTo-Json -Depth 4 | Out-File -FilePath $metaPath -Encoding utf8

Write-Host "OK saved $($briefFiles.Count) report(s) to $snapshotDir" -ForegroundColor Green
