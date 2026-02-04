# CosmosDB Emulator Network Access Setup
# Run from project root. Makes CosmosDB accessible from Azure Function and URL.

param(
    [switch]$SkipFirewall,
    [switch]$SkipRestart
)

$ErrorActionPreference = "Continue"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "CosmosDB Emulator Network Access Setup" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

# Step 1: Get Host IP Address
Write-Host "`n[1/6] Getting host IP address..." -ForegroundColor Yellow
$hostIP = "localhost"
try {
    $adapters = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
        $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*"
    }
    if ($adapters) {
        $first = $adapters | Select-Object -First 1
        $hostIP = $first.IPAddress
        Write-Host "   OK Host IP: $hostIP" -ForegroundColor Green
    }
    else {
        Write-Host "   Using localhost" -ForegroundColor Gray
    }
}
catch {
    Write-Host "   Using localhost ($($_.Exception.Message))" -ForegroundColor Gray
}

# Step 2: Stop existing containers
if (-not $SkipRestart) {
    Write-Host "`n[2/6] Stopping existing CosmosDB container..." -ForegroundColor Yellow
    try {
        $out = podman ps -a --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>&1
        if ($out -and "$out".Trim() -eq "cosmosdb-emulator") {
            podman stop cosmosdb-emulator 2>&1 | Out-Null
            podman rm cosmosdb-emulator 2>&1 | Out-Null
            Write-Host "   OK Containers stopped" -ForegroundColor Green
        }
        else {
            Write-Host "   OK No container to stop" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "   Warning: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    Write-Host "`n[2/6] Skipping restart (-SkipRestart)" -ForegroundColor Yellow
}

# Step 3: Start container if not running
Write-Host "`n[3/6] Checking CosmosDB container..." -ForegroundColor Yellow
$running = $null
try {
    $running = podman ps --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>&1
}
catch { }

if (-not $running -or "$running".Trim() -ne "cosmosdb-emulator") {
    Write-Host "   Starting CosmosDB emulator..." -ForegroundColor Gray
    try {
        podman network create aspire-network 2>&1 | Out-Null
    }
    catch { }

    $cmd = @(
        "run", "-d",
        "--name", "cosmosdb-emulator",
        "--hostname", "cosmosdb-emulator",
        "--network", "aspire-network",
        "-p", "8081:8081",
        "-p", "1234:1234",
        "-v", "cosmosdb-data:/tmp/cosmosdb-emulator-data",
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest"
    )
    $runResult = & podman @cmd 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "   FAILED to start: $runResult" -ForegroundColor Red
        Write-Host "   Try: .\start-emulators.bat" -ForegroundColor Yellow
    }
    else {
        Write-Host "   OK Container started. Waiting 30s for init..." -ForegroundColor Green
        Start-Sleep -Seconds 30
    }
}
else {
    Write-Host "   OK CosmosDB emulator already running" -ForegroundColor Green
}

# Step 4: Verify
Write-Host "`n[4/6] Verifying..." -ForegroundColor Yellow
try {
    $ports = podman port cosmosdb-emulator 2>&1
    if ($ports -match "8081") { Write-Host "   OK Port 8081 (API) exposed" -ForegroundColor Green }
    if ($ports -match "1234") { Write-Host "   OK Port 1234 (Explorer) exposed" -ForegroundColor Green }
    if ($ports -notmatch "8081") { Write-Host "   Warning: Could not verify ports" -ForegroundColor Yellow }
}
catch {
    Write-Host "   Warning: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Step 5: Firewall (optional)
if (-not $SkipFirewall) {
    Write-Host "`n[5/6] Firewall (ports 8081, 1234)..." -ForegroundColor Yellow
    try {
        foreach ($port in 8081, 1234) {
            $existing = Get-NetFirewallRule -DisplayName "CosmosDB Emulator $port" -ErrorAction SilentlyContinue
            if (-not $existing) {
                New-NetFirewallRule -DisplayName "CosmosDB Emulator $port" -Direction Inbound -LocalPort $port -Protocol TCP -Action Allow -ErrorAction Stop | Out-Null
                Write-Host "   OK Port $port rule added" -ForegroundColor Green
            }
            else { Write-Host "   OK Port $port rule exists" -ForegroundColor Green }
        }
    }
    catch {
        Write-Host "   Skip (run as Admin to add rule): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    Write-Host "`n[5/6] Firewall skipped (-SkipFirewall)" -ForegroundColor Yellow
}

# Step 6: Test URL (Linux emulator: Explorer is on 1234, not 8081/_explorer)
Write-Host "`n[6/6] Testing Explorer URL..." -ForegroundColor Yellow
try {
    $r = Invoke-WebRequest -Uri "http://localhost:1234" -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
    Write-Host "   OK Data Explorer: $($r.StatusCode)" -ForegroundColor Green
}
catch {
    Write-Host "   Explorer test failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Emulator may still be starting. Wait 1 min and try: http://localhost:1234" -ForegroundColor Yellow
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Configuration Complete" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "`nCosmos DB (Linux emulator):" -ForegroundColor Yellow
Write-Host "  API:      https://127.0.0.1:8081" -ForegroundColor Cyan
Write-Host "  Explorer: http://localhost:1234" -ForegroundColor Cyan
if ($hostIP -ne "localhost") {
    Write-Host "  Explorer (remote): http://${hostIP}:1234" -ForegroundColor Cyan
}
Write-Host "`nConnection string (local.settings.json):" -ForegroundColor Yellow
Write-Host '  "CosmosDBConnection": "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"' -ForegroundColor White
if ($hostIP -ne "localhost") {
    Write-Host "`nFor remote machine use:" -ForegroundColor Yellow
    Write-Host "  AccountEndpoint=https://${hostIP}:8081/" -ForegroundColor White
}
Write-Host "`n========================================" -ForegroundColor Cyan
