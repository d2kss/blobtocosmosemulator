# PowerShell script to start Event Hub emulator via Aspire AppHost

Write-Host "Starting Event Hub Emulator via Aspire AppHost..." -ForegroundColor Green

# Navigate to AppHost directory
$appHostPath = Join-Path $PSScriptRoot "..\BlobToCosmosFunction.AppHost"
if (-not (Test-Path $appHostPath)) {
    Write-Host "Error: AppHost directory not found at $appHostPath" -ForegroundColor Red
    exit 1
}

Write-Host "AppHost path: $appHostPath" -ForegroundColor Cyan

# Check if dotnet is available
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "Error: .NET SDK is not installed or not in PATH" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "To start the Event Hub emulator, run the AppHost project:" -ForegroundColor Yellow
Write-Host "  1. Open the solution in Visual Studio or VS Code" -ForegroundColor Cyan
Write-Host "  2. Set 'BlobToCosmosFunction.AppHost' as the startup project" -ForegroundColor Cyan
Write-Host "  3. Press F5 or run: dotnet run --project BlobToCosmosFunction.AppHost" -ForegroundColor Cyan
Write-Host ""
Write-Host "Or run from command line:" -ForegroundColor Yellow
Write-Host "  cd BlobToCosmosFunction.AppHost" -ForegroundColor Cyan
Write-Host "  dotnet run" -ForegroundColor Cyan
Write-Host ""
Write-Host "The Event Hub emulator will start automatically when the AppHost runs." -ForegroundColor Green
Write-Host "AppHost Dashboard will be available at: https://localhost:17134" -ForegroundColor Cyan
Write-Host ""

# Optionally, try to run it directly
$response = Read-Host "Do you want to start the AppHost now? (Y/N)"
if ($response -eq "Y" -or $response -eq "y") {
    Write-Host "Starting AppHost..." -ForegroundColor Green
    Set-Location $appHostPath
    dotnet run
}
