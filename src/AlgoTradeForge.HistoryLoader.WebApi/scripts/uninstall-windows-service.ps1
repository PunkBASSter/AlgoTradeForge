#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the AlgoTradeForge HistoryLoader Windows Service.

.DESCRIPTION
    Stops the service if running, then deletes it.
    Does NOT delete published files or data.

.NOTES
    Run this script from an elevated (Administrator) PowerShell prompt.
#>

$ErrorActionPreference = 'Stop'

$ServiceName = 'AlgoTradeForge.HistoryLoader'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' not found. Nothing to do." -ForegroundColor Yellow
    return
}

Write-Host "Stopping service ..." -ForegroundColor Yellow
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue

Write-Host "Removing service ..." -ForegroundColor Yellow
sc.exe delete $ServiceName | Out-Null
Start-Sleep -Seconds 2

Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
Write-Host ""
Write-Host "Published files are still at:" -ForegroundColor Cyan
Write-Host "  $(Join-Path $env:LOCALAPPDATA 'AlgoTradeForge\HistoryLoader')"
