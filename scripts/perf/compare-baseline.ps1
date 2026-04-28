<#
.SYNOPSIS
    Diffs two perf-history snapshots created by save-baseline.ps1.

.DESCRIPTION
    Resolves -Baseline / -Candidate as either:
      - an absolute or relative path to a snapshot dir, OR
      - a snapshot dir name under ~/.algo-tradeforge/perf-history/, OR
      - 'latest' / 'previous' to pick by mtime.

    Joins by (Type.Method, Parameters), prints Mean/Allocated deltas, and
    cross-checks that the runs came from the same machine + filter so the
    comparison isn't apples-to-oranges.

.EXAMPLE
    powershell.exe -File scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Baseline,
    [Parameter(Mandatory)] [string]$Candidate
)

$ErrorActionPreference = 'Stop'
$historyRoot = Join-Path $HOME '.algo-tradeforge\perf-history'

function Resolve-Snapshot([string]$ref) {
    if (Test-Path $ref -PathType Container) { return (Resolve-Path $ref).Path }
    $direct = Join-Path $historyRoot $ref
    if (Test-Path $direct -PathType Container) { return $direct }
    if ($ref -eq 'latest' -or $ref -eq 'previous') {
        $all = @(Get-ChildItem -Path $historyRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)
        if ($all.Count -lt 1) { throw "No snapshots in $historyRoot." }
        if ($ref -eq 'latest') { return $all[0].FullName }
        if ($all.Count -lt 2) { throw "Only one snapshot in history; 'previous' needs at least two." }
        return $all[1].FullName
    }
    throw "Could not resolve snapshot '$ref'. Tried: literal path, $historyRoot/$ref, and aliases (latest|previous)."
}

# Returns a hashtable: Key (Type.Method|Parameters) -> [pscustomobject].
# Inlined instead of a returning function because PS 5.1 unwrapping
# inside [pscustomobject]@{...} closures has bitten us before.
function Get-SnapshotMap([string]$dir) {
    $map = @{}
    $files = @(Get-ChildItem -Path $dir -Filter '*-report-brief.json' -File)
    foreach ($f in $files) {
        $json = Get-Content -Raw -Path $f.FullName | ConvertFrom-Json
        $entries = @($json.Benchmarks)
        foreach ($b in $entries) {
            if ($null -eq $b) { continue }
            $key = ("{0}.{1}|{2}" -f $b.Type, $b.Method, $b.Parameters)
            $map[$key] = @{
                Type       = [string]$b.Type
                Method     = [string]$b.Method
                Parameters = [string]$b.Parameters
                MeanNs     = [double]$b.Statistics.Mean
                StdErrNs   = [double]$b.Statistics.StandardError
                AllocBytes = [long]$b.Memory.BytesAllocatedPerOperation
            }
        }
    }
    return $map
}

function Read-Meta([string]$dir) {
    $p = Join-Path $dir 'metadata.json'
    if (Test-Path $p) { return Get-Content -Raw $p | ConvertFrom-Json }
    return $null
}

$baseDir = Resolve-Snapshot $Baseline
$candDir = Resolve-Snapshot $Candidate
Write-Host "baseline:  $baseDir"
Write-Host "candidate: $candDir"

$bMeta = Read-Meta $baseDir
$cMeta = Read-Meta $candDir
if ($bMeta -and $cMeta) {
    if ($bMeta.machine -ne $cMeta.machine) {
        Write-Warning "Machine mismatch: baseline=$($bMeta.machine), candidate=$($cMeta.machine). Numbers are not directly comparable."
    }
    if ($bMeta.filter -ne $cMeta.filter) {
        Write-Warning "Filter mismatch: baseline='$($bMeta.filter)', candidate='$($cMeta.filter)'."
    }
    if ($bMeta.job -ne $cMeta.job -or $bMeta.job -eq 'dry' -or $cMeta.job -eq 'dry') {
        Write-Warning "Job=dry detected; results are smoke-test only, not real measurements."
    }
}

$baseMap = Get-SnapshotMap $baseDir
$candMap = Get-SnapshotMap $candDir

$rowList = New-Object System.Collections.Generic.List[object]
foreach ($key in $candMap.Keys) {
    $c = $candMap[$key]
    if ($baseMap.ContainsKey($key)) {
        $b = $baseMap[$key]
        $meanPct  = ($c.MeanNs - $b.MeanNs) / $b.MeanNs * 100
        $allocPct = 0
        if ($b.AllocBytes -ne 0) { $allocPct = ($c.AllocBytes - $b.AllocBytes) / $b.AllocBytes * 100 }
        $rowList.Add([pscustomobject]@{
            Bench      = ("{0}.{1}" -f $c.Type, $c.Method)
            Params     = $c.Parameters
            BaseMean   = ('{0:N0} ns' -f $b.MeanNs)
            CandMean   = ('{0:N0} ns' -f $c.MeanNs)
            MeanDelta  = ('{0:+0.00;-0.00}%' -f $meanPct)
            BaseAlloc  = ('{0:N0} B' -f $b.AllocBytes)
            CandAlloc  = ('{0:N0} B' -f $c.AllocBytes)
            AllocDelta = ('{0:+0.00;-0.00}%' -f $allocPct)
        }) | Out-Null
    } else {
        $rowList.Add([pscustomobject]@{
            Bench      = ("{0}.{1}" -f $c.Type, $c.Method)
            Params     = $c.Parameters
            BaseMean   = '-'
            CandMean   = ('{0:N0} ns' -f $c.MeanNs)
            MeanDelta  = 'NEW'
            BaseAlloc  = '-'
            CandAlloc  = ('{0:N0} B' -f $c.AllocBytes)
            AllocDelta = 'NEW'
        }) | Out-Null
    }
}

if ($rowList.Count -eq 0) { Write-Host '(no benchmarks matched)'; exit 0 }
$rowList | Format-Table -AutoSize

Write-Host ''
Write-Host '## Perf summary' -ForegroundColor Cyan
foreach ($r in $rowList) {
    Write-Host "- $($r.Bench): Mean $($r.BaseMean) -> $($r.CandMean) ($($r.MeanDelta)); Alloc $($r.BaseAlloc) -> $($r.CandAlloc) ($($r.AllocDelta))"
}
