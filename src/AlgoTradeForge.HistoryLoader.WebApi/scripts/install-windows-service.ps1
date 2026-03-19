#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes and installs AlgoTradeForge HistoryLoader as a Windows Service.

.DESCRIPTION
    - Publishes via the Windows-LocalAppData publish profile
    - Stops/removes any existing service with the same name
    - Pins DataRoot to the installing user's %LOCALAPPDATA% (avoids LocalSystem path mismatch)
    - Creates the service with auto-start
    - Sets ASPNETCORE_ENVIRONMENT=Production via registry
    - Starts the service

.NOTES
    Run this script from an elevated (Administrator) PowerShell prompt.
#>

$ErrorActionPreference = 'Stop'

$ServiceName = 'AlgoTradeForge.HistoryLoader'
$DisplayName = 'AlgoTradeForge History Loader'
$ProjectDir  = Split-Path -Parent $PSScriptRoot
$PublishDir   = Join-Path $env:LOCALAPPDATA 'AlgoTradeForge\HistoryLoader'
$ExePath      = Join-Path $PublishDir 'AlgoTradeForge.HistoryLoader.WebApi.exe'

# 1. Publish
Write-Host "Publishing to $PublishDir ..." -ForegroundColor Cyan
dotnet publish $ProjectDir `
    -c Release `
    -r win-x64 `
    --self-contained false `
    /p:PublishReadyToRun=true `
    /p:EnvironmentName=Production `
    -o $PublishDir `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# 2. Pin DataRoot to the installing user's LocalAppData
#    Without this, a LocalSystem service resolves %LOCALAPPDATA% to the system profile directory.
$prodSettings = Join-Path $PublishDir 'appsettings.Production.json'
$json = Get-Content $prodSettings -Raw | ConvertFrom-Json
if (-not $json.HistoryLoader) {
    $json | Add-Member -NotePropertyName 'HistoryLoader' -NotePropertyValue ([PSCustomObject]@{})
}
$dataRoot = Join-Path $env:LOCALAPPDATA 'AlgoTradeForge\History'
$json.HistoryLoader | Add-Member -NotePropertyName 'DataRoot' -NotePropertyValue $dataRoot -Force
$json | ConvertTo-Json -Depth 10 | Set-Content $prodSettings -Encoding UTF8
Write-Host "Pinned DataRoot to $dataRoot" -ForegroundColor Green

# 3. Stop/remove existing service (if reinstalling)
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service ..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Removed existing service." -ForegroundColor Yellow
}

# 4. Create service
Write-Host "Creating service ..." -ForegroundColor Cyan
sc.exe create $ServiceName `
    binPath= "`"$ExePath`"" `
    start= auto `
    DisplayName= "`"$DisplayName`""
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE" }

# 5. Set environment variable via registry
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $regPath -Name 'Environment' -Value @('ASPNETCORE_ENVIRONMENT=Production') -Type MultiString
Write-Host "Set ASPNETCORE_ENVIRONMENT=Production" -ForegroundColor Green

# 6. Set description
sc.exe description $ServiceName "Collects historical market data from Binance (klines, funding rates, OI, liquidations, etc.)" | Out-Null

# 7. Start the service
Write-Host "Starting service ..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

# 8. Print status
Write-Host ""
Write-Host "Service installed and started!" -ForegroundColor Green
Get-Service -Name $ServiceName | Format-Table Name, Status, StartType -AutoSize

Write-Host ""
Write-Host "Useful commands:" -ForegroundColor Cyan
Write-Host "  Get-Service $ServiceName"
Write-Host "  Restart-Service $ServiceName"
Write-Host "  Stop-Service $ServiceName"
Write-Host "  Get-Content '$PublishDir\logs\history-loader-*.log' -Tail 50"
Write-Host "  Invoke-RestMethod http://localhost:5210/health"
