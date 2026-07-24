#!/usr/bin/env pwsh
# Baut und startet MatCMS via Docker Compose.
# "pull: true" in docker-compose.yml sorgt dafuer, dass die Basis-Images immer neu gezogen werden.
$ErrorActionPreference = "Stop"

Write-Host "Baue & starte MatCMS (Repull der Basis-Images) ..." -ForegroundColor Cyan
docker compose up -d --build

Write-Host ""
Write-Host "Laeuft auf http://localhost:9101" -ForegroundColor Green
Write-Host "Admin-Login: http://localhost:9101/login  (admin / admin)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Logs anzeigen mit:  docker compose logs -f" -ForegroundColor DarkGray
