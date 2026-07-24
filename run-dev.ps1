#!/usr/bin/env pwsh
# Lokale Entwicklung mit Hot Reload. Haelt Code + Ansicht automatisch aktuell.
$ErrorActionPreference = "Stop"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_HTTP_PORTS = "9101"

Write-Host "MatCMS startet im Entwicklungsmodus (Hot Reload) auf http://localhost:9101 ..." -ForegroundColor Cyan
Write-Host "Admin-Login: http://localhost:9101/login  (admin / admin)" -ForegroundColor DarkGray

# Neueste NuGet-Pakete ziehen (10.0.*), danach mit dotnet watch starten.
dotnet restore
dotnet watch run
