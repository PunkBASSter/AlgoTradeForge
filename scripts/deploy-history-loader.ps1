<#
.SYNOPSIS
    Publishes the AlgoTradeForge HistoryLoader WebApi to InstallPath, installs it as a
    Windows Service if not already installed, and (re)starts it.

.DESCRIPTION
    Idempotent deploy flow:
      1. Self-elevates (writes to HKLM and sc.exe both require Administrator).
      2. Stops the service if it exists and is running so publish doesn't hit file locks.
      3. dotnet publishes the WebApi to InstallPath.
      4. If the service is missing, creates it via sc.exe with auto-start + auto-restart.
         If it exists but its BinPath drifted from InstallPath, updates BinPath.
      5. Writes service-scoped environment variables under
         HKLM:\SYSTEM\CurrentControlSet\Services\<name>\Environment:
            ASPNETCORE_ENVIRONMENT=Production
            HistoryLoader__DataRoot=<DataRoot>          (binds to HistoryLoader:DataRoot)
         Preserves any unrelated env entries already set on the service.
      6. Starts the service.

    The HistoryLoader__DataRoot env var beats appsettings.Production.json — so source
    config can stay machine-agnostic while the deployed service writes to the
    user-profile data dir even when running as LocalSystem.

.PARAMETER InstallPath
    Where the published binaries live (also the service BinPath).
    Default: C:\Users\Andrew\AppData\Local\AlgoTradeForge\HistoryLoader

.PARAMETER DataRoot
    Where collected feed data is written. Bound to HistoryLoader:DataRoot.
    Default: C:\Users\Andrew\AppData\Local\AlgoTradeForge\History

.PARAMETER Configuration
    Build configuration: Debug or Release. Default: Release.

.PARAMETER Elevated
    Internal: set automatically when the script self-elevates so the elevated child
    pauses before closing the console window. Do not pass manually.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\deploy-history-loader.ps1

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\deploy-history-loader.ps1 `
        -InstallPath D:\Apps\HistoryLoader -DataRoot D:\Data\AlgoTradeForge\History
#>
[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\Users\Andrew\AppData\Local\AlgoTradeForge\HistoryLoader',
    [string]$DataRoot = 'C:\Users\Andrew\AppData\Local\AlgoTradeForge\History',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'

$ServiceName        = 'AlgoTradeForge.HistoryLoader'
$ServiceDisplayName = 'AlgoTradeForge History Loader'
$ServiceDescription = 'Binance historical and live data collector for AlgoTradeForge.'

# --- Self-elevate ---------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '[deploy] Re-launching elevated...' -ForegroundColor Yellow
    $argList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$($MyInvocation.MyCommand.Path)`"",
        '-InstallPath',   "`"$InstallPath`"",
        '-DataRoot',      "`"$DataRoot`"",
        '-Configuration', $Configuration,
        '-Elevated'
    )
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

# --- Resolve repo root from script location -------------------------------------
$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectDir  = Join-Path $repoRoot 'src\AlgoTradeForge.HistoryLoader.WebApi'
$projectFile = Join-Path $projectDir 'AlgoTradeForge.HistoryLoader.WebApi.csproj'
if (-not (Test-Path $projectFile)) {
    Write-Error "HistoryLoader project not found at $projectFile. Run from the AlgoTradeForge repo."
    exit 1
}

$exePath = Join-Path $InstallPath 'AlgoTradeForge.HistoryLoader.WebApi.exe'

Write-Host ''
Write-Host '[deploy] Configuration' -ForegroundColor Cyan
Write-Host "  Service        : $ServiceName"
Write-Host "  Install path   : $InstallPath"
Write-Host "  Data root      : $DataRoot"
Write-Host "  Build config   : $Configuration"
Write-Host "  Repo root      : $repoRoot"
Write-Host ''

# --- Stop service if running ----------------------------------------------------
$svc     = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$existed = $null -ne $svc
if ($existed) {
    Write-Host "[deploy] Existing service: status = $($svc.Status)" -ForegroundColor Gray
    if ($svc.Status -ne 'Stopped') {
        Write-Host '[deploy] Stopping service...' -ForegroundColor Cyan
        Stop-Service -Name $ServiceName -Force
        # Stop-Service returns before the process unwinds on rare slow shutdowns;
        # poll until the process is gone so publish doesn't hit a held DLL handle.
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 30) {
            $procs = Get-Process -Name 'AlgoTradeForge.HistoryLoader.WebApi' -ErrorAction SilentlyContinue
            if (-not $procs) { break }
            Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Name 'AlgoTradeForge.HistoryLoader.WebApi' -ErrorAction SilentlyContinue) {
            Write-Error 'Service process did not exit within 30 s — refusing to publish over a live install.'
            exit 1
        }
    }
} else {
    Write-Host '[deploy] Service is not installed; will register after publish.' -ForegroundColor Gray
}

# --- Publish --------------------------------------------------------------------
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Write-Host "[deploy] Created install dir: $InstallPath" -ForegroundColor Yellow
}

Write-Host '[deploy] Publishing...' -ForegroundColor Cyan
& dotnet publish $projectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $InstallPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed (exit $LASTEXITCODE)."
    exit 1
}
if (-not (Test-Path $exePath)) {
    Write-Error "Expected exe not found after publish: $exePath"
    exit 1
}

# --- Register or update service -------------------------------------------------
if (-not $existed) {
    Write-Host '[deploy] Registering service...' -ForegroundColor Cyan
    # sc.exe syntax: option= value (space *after* =, value as next token).
    # Backtick-escaped quotes embed real quotes into the argument so paths with spaces
    # would still parse — defensive, even though the default path has no spaces.
    & sc.exe create $ServiceName `
        binPath= "`"$exePath`"" `
        start= auto `
        DisplayName= "`"$ServiceDisplayName`"" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "sc.exe create failed (exit $LASTEXITCODE)."
        exit 1
    }
    & sc.exe description $ServiceName "`"$ServiceDescription`"" | Out-Null
    # Restart on crash: 5s, 30s, 60s; reset failure counter daily.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
    Write-Host '[deploy] Service registered.' -ForegroundColor Green
} else {
    # Reconcile BinPath if it drifted (e.g. caller passed a new -InstallPath).
    $svcCim         = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $currentBinPath = $svcCim.PathName.Trim('"')
    if ($currentBinPath -ne $exePath) {
        Write-Host "[deploy] BinPath drift detected; updating..." -ForegroundColor Yellow
        Write-Host "  was: $currentBinPath"
        Write-Host "  now: $exePath"
        & sc.exe config $ServiceName binPath= "`"$exePath`"" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Error "sc.exe config (binPath update) failed."
            exit 1
        }
    }
}

# --- Set service-scoped environment variables -----------------------------------
$regPath    = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$currentEnv = @()
try {
    $currentEnv = @((Get-ItemProperty -Path $regPath -Name Environment -ErrorAction Stop).Environment)
} catch {
    # No Environment value yet — first install, will be created below.
}

$desiredEnv = [ordered]@{
    'ASPNETCORE_ENVIRONMENT'   = 'Production'
    'HistoryLoader__DataRoot'  = $DataRoot
}

# Preserve unrelated entries (anything not in $desiredEnv keeps its existing value).
$preserved = $currentEnv | Where-Object {
    $kv = $_ -split '=', 2
    $kv.Length -eq 2 -and -not $desiredEnv.Contains($kv[0])
}
$newEnv = @($preserved) + ($desiredEnv.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })

Set-ItemProperty -Path $regPath -Name Environment -Value $newEnv -Type MultiString
Write-Host '[deploy] Service environment:' -ForegroundColor Cyan
(Get-ItemProperty -Path $regPath -Name Environment).Environment | ForEach-Object { Write-Host "  $_" }

# --- Ensure data root exists ----------------------------------------------------
if (-not (Test-Path $DataRoot)) {
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    Write-Host "[deploy] Created data root: $DataRoot" -ForegroundColor Yellow
}

# --- Start service --------------------------------------------------------------
Write-Host '[deploy] Starting service...' -ForegroundColor Cyan
Start-Service -Name $ServiceName

$final = Get-Service -Name $ServiceName
Write-Host ''
Write-Host "[deploy] Done. $ServiceName -> $($final.Status)" -ForegroundColor Green
Write-Host "  Logs   : $InstallPath\logs\history-loader-<date>.log"
Write-Host "  Health : http://localhost:5210/health"
Write-Host ''

if ($Elevated) {
    Write-Host 'Press Enter to close.' -ForegroundColor DarkGray
    [void](Read-Host)
}
