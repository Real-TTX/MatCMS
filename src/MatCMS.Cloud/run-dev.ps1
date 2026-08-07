#!/usr/bin/env pwsh
# Lokale Entwicklung mit Hot Reload. Haelt Code + Ansicht automatisch aktuell.
$ErrorActionPreference = "Stop"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_HTTP_PORTS = "9100"
# Docker Desktop unter Windows spricht ueber eine Named Pipe statt eines Unix-Sockets.
if (-not $env:MatCmsCloud__Docker__Endpoint) {
    $env:MatCmsCloud__Docker__Endpoint = "npipe://./pipe/docker_engine"
}

Write-Host "MatCMS.Cloud startet im Entwicklungsmodus (Hot Reload) auf http://localhost:9100 ..." -ForegroundColor Cyan
Write-Host "Admin-Login: http://localhost:9100/login  (admin / admin)" -ForegroundColor DarkGray

# Neueste NuGet-Pakete ziehen (10.0.*), danach mit dotnet watch starten.
dotnet restore
dotnet watch run
