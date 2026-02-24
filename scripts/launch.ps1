<#
.SYNOPSIS
    Launches AlgoTradeForge backend and optionally the frontend.
    Used by Visual Studio launch profiles for compound configurations.

.PARAMETER BackendConfig
    Build configuration for the backend: Debug or Release.

.PARAMETER FrontendMode
    Frontend launch mode: dev (Next.js dev server), release (production build + serve), or none.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("Debug", "Release")]
    [string]$BackendConfig,

    [ValidateSet("dev", "release", "none")]
    [string]$FrontendMode = "none"
)

$ErrorActionPreference = "Stop"

# Resolve repo root from script location
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if (-not (Test-Path (Join-Path $repoRoot "AlgoTradeForge.slnx"))) {
    # Fallback: maybe called with workingDirectory = repo root
    if (Test-Path (Join-Path (Get-Location) "AlgoTradeForge.slnx")) {
        $repoRoot = (Get-Location).Path
    }
    else {
        Write-Error "Cannot locate AlgoTradeForge.slnx. Run from repo root or scripts/ folder."
        exit 1
    }
}

$frontendDir = Join-Path $repoRoot "frontend"
$backendProject = Join-Path $repoRoot "src" "AlgoTradeForge.WebApi"
$frontendProcess = $null

function Stop-Frontend {
    if ($script:frontendProcess -and -not $script:frontendProcess.HasExited) {
        Write-Host "`n[Frontend] Stopping (PID $($script:frontendProcess.Id))..." -ForegroundColor Yellow
        taskkill /T /F /PID $script:frontendProcess.Id 2>$null
    }
}

try {
    # --- Frontend ---
    if ($FrontendMode -ne "none") {
        if ($FrontendMode -eq "release") {
            Write-Host "[Frontend] Building for production..." -ForegroundColor Cyan
            $buildResult = Start-Process -FilePath "cmd.exe" `
                -ArgumentList "/c cd /d `"$frontendDir`" && npm run build" `
                -Wait -PassThru -NoNewWindow
            if ($buildResult.ExitCode -ne 0) { throw "Frontend build failed." }

            Write-Host "[Frontend] Starting production server on http://localhost:3000" -ForegroundColor Cyan
            $frontendProcess = Start-Process -FilePath "cmd.exe" `
                -ArgumentList "/c title AlgoTradeForge Frontend && cd /d `"$frontendDir`" && npm run start" `
                -PassThru
        }
        else {
            Write-Host "[Frontend] Starting dev server on http://localhost:3000" -ForegroundColor Cyan
            $frontendProcess = Start-Process -FilePath "cmd.exe" `
                -ArgumentList "/c title AlgoTradeForge Frontend && cd /d `"$frontendDir`" && npm run dev" `
                -PassThru
        }

        Start-Sleep -Seconds 2
        Write-Host "[Frontend] Running (PID $($frontendProcess.Id))" -ForegroundColor Green
    }

    # --- Backend ---
    $env:ASPNETCORE_ENVIRONMENT = if ($BackendConfig -eq "Debug") { "Development" } else { "Production" }

    Write-Host "[Backend] Starting ($BackendConfig, $env:ASPNETCORE_ENVIRONMENT)..." -ForegroundColor Cyan
    Write-Host "[Backend] https://localhost:55908 | http://localhost:55909" -ForegroundColor Cyan
    Write-Host ""

    & dotnet run --project $backendProject -c $BackendConfig
}
finally {
    Stop-Frontend
}
