<#
.SYNOPSIS
  Stops what scripts\demo.ps1 started: the backend and frontend windows and the compose infrastructure.
.PARAMETER Volumes
  Also delete the database and monitoring volumes (all local data).
#>
[CmdletBinding()]
param([switch]$Volumes)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$stateDir = Join-Path $env:TEMP 'beko-demo'

foreach ($name in 'frontend', 'backend') {
    $pidFile = Join-Path $stateDir "$name.pid"
    if (Test-Path $pidFile) {
        $procId = [int](Get-Content $pidFile)
        if (Get-Process -Id $procId -ErrorAction SilentlyContinue) {
            Write-Host "[demo] stopping $name (pid $procId and children)"
            taskkill /PID $procId /T /F *> $null
        }
        Remove-Item $pidFile -Force
    }
}

# Anything still listening on the app ports (e.g. a dotnet/node left by a closed window).
foreach ($port in 5173, 5080) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
        $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -in 'dotnet', 'node', 'Beko.Agents.Api') {
            Write-Host "[demo] stopping $($p.ProcessName) on :$port (pid $($p.Id))"
            taskkill /PID $p.Id /T /F *> $null
        }
    }
}

if (Get-Command docker -ErrorAction SilentlyContinue) {
    if ($Volumes) { docker compose down -v } else { docker compose down }
}
Write-Host '[demo] stopped.'
