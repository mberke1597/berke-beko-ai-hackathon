<#
.SYNOPSIS
  One-command local demo: infrastructure in Docker, backend and frontend from source.
.DESCRIPTION
  Checks Docker, dotnet and node; creates .env from .env.example if missing; starts the compose
  infrastructure (postgres, mongodb, otel-collector, prometheus, tempo, loki, grafana); starts the
  backend (dotnet run, simulator time scale 60) and the frontend (npm run dev) in minimized windows;
  waits until both answer; prints the URLs. Stop everything with scripts\demo-stop.ps1.
  The Anthropic key is read from the ANTHROPIC_API_KEY environment variable or from .env and is never printed.
#>
[CmdletBinding()]
param(
    [switch]$NoFrontend,
    [int]$TimeScale = 60
)

# Native tools write progress to stderr; exit codes are checked explicitly, so do not turn stderr into errors.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$stateDir = Join-Path $env:TEMP 'beko-demo'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

function Info($m) { Write-Host "[demo] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[demo] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "[demo] $m" -ForegroundColor Red; exit 1 }

function Test-Tool($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { Fail "'$name' not found. $hint" }
}

function Test-PortFree([int]$port) {
    # Listeners on ::1 only (Vite on 'localhost') are not caught by a bind test on 127.0.0.1.
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { return $false }
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    try { $l.Start(); $l.Stop(); return $true } catch { return $false }
}

function Get-PortOwner([int]$port) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return 'unknown process' }
    $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
    if ($p) { return "$($p.ProcessName) (pid $($p.Id))" }
    return "pid $($c.OwningProcess)"
}

function Wait-Http($url, $seconds, $what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Seconds 2
    }
    Fail "$what did not become ready within $seconds s ($url). See the window or log in $stateDir."
}

# 1. Prerequisites ---------------------------------------------------------------------------------
Test-Tool docker 'Install Docker Desktop and start it.'
Test-Tool dotnet 'Install the .NET 10 SDK.'
Test-Tool node 'Install Node.js 22 or newer.'
Test-Tool npm 'Install Node.js 22 or newer.'
docker info *> $null
if ($LASTEXITCODE -ne 0) { Fail 'Docker daemon is not running. Start Docker Desktop and retry.' }
Info "dotnet $(dotnet --version), node $(node --version), docker ok"

# 2. .env ------------------------------------------------------------------------------------------
if (-not (Test-Path .env)) {
    Copy-Item .env.example .env
    Info 'Created .env from .env.example (placeholders; set ANTHROPIC_API_KEY there for the AI features).'
}
$dotenv = @{}
Get-Content .env | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $dotenv[$Matches[1]] = $Matches[2].Trim() }
}
function Get-Setting($name, $default = '') {
    $v = [Environment]::GetEnvironmentVariable($name)
    if (-not [string]::IsNullOrWhiteSpace($v)) { return $v }
    if ($dotenv.ContainsKey($name) -and $dotenv[$name] -ne '') { return $dotenv[$name] }
    return $default
}

# 3. Database host ports (a local Postgres/MongoDB may already use 5432/27017) ----------------------
function Resolve-HostPort($service, $containerPort, $envName, $preferred, $fallback) {
    $explicit = Get-Setting $envName ''
    if ($explicit) { return [int]$explicit }
    $published = docker compose port $service $containerPort 2>$null
    if ($LASTEXITCODE -eq 0 -and $published -match ':(\d+)\s*$') { return [int]$Matches[1] }
    if (Test-PortFree $preferred) { return $preferred }
    Warn "Port $preferred is held by $(Get-PortOwner $preferred); using $fallback for $service."
    return $fallback
}
$pgPort = Resolve-HostPort 'postgres' 5432 'POSTGRES_HOST_PORT' 5432 15432
$mongoPort = Resolve-HostPort 'mongodb' 27017 'MONGO_HOST_PORT' 27017 27018
$env:POSTGRES_HOST_PORT = "$pgPort"
$env:MONGO_HOST_PORT = "$mongoPort"

# 4. Infrastructure --------------------------------------------------------------------------------
Info 'Starting infrastructure (postgres, mongodb, otel-collector, prometheus, tempo, loki, grafana) ...'
docker compose up -d
if ($LASTEXITCODE -ne 0) { Fail 'docker compose up failed.' }
$deadline = (Get-Date).AddSeconds(120)
foreach ($svc in 'postgres', 'mongodb') {
    while ($true) {
        $id = docker compose ps -q $svc
        $health = if ($id) { docker inspect --format '{{.State.Health.Status}}' $id } else { '' }
        if ($health -eq 'healthy') { break }
        if ((Get-Date) -gt $deadline) { Fail "$svc is not healthy (docker compose logs $svc)." }
        Start-Sleep -Seconds 2
    }
}
Info "Databases healthy (postgres :$pgPort, mongodb :$mongoPort)."

# 5. Backend ---------------------------------------------------------------------------------------
if (-not (Test-PortFree 5080)) {
    try { Invoke-WebRequest -Uri http://localhost:5080/health -UseBasicParsing -TimeoutSec 3 | Out-Null; Warn 'Something already answers on :5080 (previous demo run?). Reusing it. Run scripts\demo-stop.ps1 first for a clean start.'; $reuseBackend = $true }
    catch { Fail "Port 5080 is held by $(Get-PortOwner 5080), which is not a Beko backend. The frontend proxy needs :5080: stop that program (or run scripts\demo-stop.ps1) and retry." }
}
if (-not $reuseBackend) {
    $pgPass = Get-Setting 'POSTGRES_PASSWORD' 'changeme'
    $mongoPass = Get-Setting 'MONGO_PASSWORD' 'changeme'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=$pgPort;Database=beko;Username=beko;Password=$pgPass"
    $env:Mongo__ConnectionString = "mongodb://beko:$mongoPass@127.0.0.1:$mongoPort/?authSource=admin"
    $env:Mongo__Database = 'beko'
    $env:Jwt__SigningKey = Get-Setting 'JWT_SIGNING_KEY' 'dev-only-signing-key-at-least-32-bytes-long'
    $env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://localhost:4317'
    $env:OTEL_SERVICE_NAME = 'beko-backend'
    $env:Simulation__TimeScale = "$TimeScale"
    $env:App__Commit = 'local-demo'
    # Key sources, in the backend's own precedence: Anthropic__ApiKey (environment), the git-ignored
    # backend\src\Beko.Agents.Apippsettings.Local.json (loaded by the backend itself), or an optional
    # ANTHROPIC_API_KEY in the environment/.env (passed through). Values are never read into output.
    $localJson = Join-Path $root 'backend\src\Beko.Agents.Apippsettings.Local.json'
    $hasLocalKey = $false
    if (Test-Path $localJson) {
        $txt = Get-Content $localJson -Raw
        if ($txt -match '"ApiKey"\s*:\s*"([^"]+)"' -and $Matches[1] -ne 'PUT-YOUR-KEY-HERE') { $hasLocalKey = $true }
    }
    $apiKey = Get-Setting 'ANTHROPIC_API_KEY' ''
    $hasEnvKey = (-not [string]::IsNullOrWhiteSpace($env:Anthropic__ApiKey)) -or ($apiKey -and $apiKey -notmatch '^(changeme|<.*>)$')
    if ($apiKey -and $apiKey -notmatch '^(changeme|<.*>)$' -and [string]::IsNullOrWhiteSpace($env:Anthropic__ApiKey)) {
        $env:Anthropic__ApiKey = $apiKey
    }
    if ($hasLocalKey -or $hasEnvKey) {
        Info 'Anthropic key found (value not shown). AI features enabled.'
    } else {
        Warn 'No Anthropic key (appsettings.Local.json is missing or still PUT-YOUR-KEY-HERE, Anthropic__ApiKey is not set): the app runs, but AI chat/voice will not work.'
    }
    Info 'Starting backend (dotnet run) in a minimized window ...'
    $be = Start-Process -FilePath powershell -WindowStyle Minimized -PassThru -WorkingDirectory (Join-Path $root 'backend') `
        -ArgumentList '-NoProfile', '-Command', "`$Host.UI.RawUI.WindowTitle='Beko backend'; dotnet run --project src/Beko.Agents.Api --urls http://localhost:5080 2>&1 | Tee-Object -FilePath '$stateDir\backend.log'"
    $be.Id | Set-Content (Join-Path $stateDir 'backend.pid')
    # The secret must not stay in this shell.
    Remove-Item Env:\Anthropic__ApiKey -ErrorAction SilentlyContinue
}

# 6. Frontend --------------------------------------------------------------------------------------
if (-not $NoFrontend) {
    $feFree = Test-PortFree 5173
    if (-not $feFree) {
        Warn "Port 5173 is held by $(Get-PortOwner 5173) (previous demo run or another Vite). Reusing it."
        $feFree = $false
    } else {
        if (-not (Test-Path (Join-Path $root 'frontend\node_modules'))) {
            Info 'Installing frontend dependencies (npm ci) ...'
            Push-Location (Join-Path $root 'frontend'); npm ci --no-audit --no-fund; Pop-Location
        }
        Info 'Starting frontend (npm run dev) in a minimized window ...'
        $fe = Start-Process -FilePath powershell -WindowStyle Minimized -PassThru -WorkingDirectory (Join-Path $root 'frontend') `
            -ArgumentList '-NoProfile', '-Command', "`$Host.UI.RawUI.WindowTitle='Beko frontend'; npm run dev 2>&1 | Tee-Object -FilePath '$stateDir\frontend.log'"
        $fe.Id | Set-Content (Join-Path $stateDir 'frontend.pid')
    }
}

# 7. Wait ------------------------------------------------------------------------------------------
Info 'Waiting for the backend (first start compiles, can take a minute) ...'
Wait-Http 'http://localhost:5080/health/ready' 240 'Backend'
if (-not $NoFrontend) { Wait-Http 'http://localhost:5173/' 90 'Frontend' }

Write-Host ''
Write-Host 'Ready.' -ForegroundColor Green
Write-Host '  App        http://localhost:5173/#/ev'
Write-Host '  Panel      http://localhost:5173/#/panel'
Write-Host '  API        http://localhost:5080/api/version'
Write-Host '  Grafana    http://localhost:3000/grafana/   (admin / GRAFANA_ADMIN_PASSWORD from .env, default changeme)'
Write-Host '  Prometheus http://localhost:9090'
Write-Host ''
Write-Host "Logs: $stateDir   Stop: scripts\demo-stop.ps1   (or: make demo-stop)"
