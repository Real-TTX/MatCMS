# Packt MatCMS.Plugin.Abstractions (die API, gegen die Plugins geschrieben werden) als NuGet-Paket in einen LOKALEN
# Feed-Ordner. Es wird nichts veroeffentlicht. Die Projektvorlage templates/matcms-native-plugin zieht das Paket von dort.
#   ./pack-abstractions.ps1                     -> <Repo>/artifacts/nuget/MatCMS.Plugin.Abstractions.<version>.nupkg
#   ./pack-abstractions.ps1 -Out C:\feed        -> eigener Feed-Ordner
# Danach in der NuGet-Konfiguration des Plugin-Projekts (nuget.config) den Ordner als Quelle eintragen, z. B.
#   dotnet new nugetconfig ; dotnet nuget add source <Out> --name matcms-local --configfile nuget.config
param([string]$Out, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src/MatCMS.Plugin.Abstractions/MatCMS.Plugin.Abstractions.csproj'
if (-not (Test-Path $proj)) { throw "Projekt nicht gefunden: $proj" }
if (-not $Out) { $Out = Join-Path $repo 'artifacts/nuget' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

dotnet pack $proj -c $Configuration -o $Out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet pack ist fehlgeschlagen (Exitcode $LASTEXITCODE)" }
$pkg = Get-ChildItem $Out -Filter 'MatCMS.Plugin.Abstractions.*.nupkg' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ("Paket: {0} ({1} Bytes)" -f $pkg.FullName, $pkg.Length)
Write-Host "Feed:  $Out"
