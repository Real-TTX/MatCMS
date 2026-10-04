# Packs plugins/<Key>/ (plugin.csx + meta.json) into a MatCMS plugin bundle — plugin.json in the exact
# shape PluginPackager.Export writes (Format 1), so the cloud store and an instance import it unchanged.
#   ./pack.ps1 -Key leserstimmen                → plugins/dist/leserstimmen-<version>.zip
#   ./pack.ps1 -Key leserstimmen -Out x.zip
# Upload to the store: POST /api/v1/store/plugins with the zip as body (key right CanManageStore).
param([Parameter(Mandatory)][string]$Key, [string]$Out)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dir = Join-Path $PSScriptRoot $Key
if (-not (Test-Path (Join-Path $dir 'plugin.csx'))) { throw "Kein Plugin unter $dir" }
$meta = Get-Content (Join-Path $dir 'meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($meta.Key -ne $Key) { throw "meta.json nennt Key '$($meta.Key)', Ordner heisst '$Key'" }
$code = [System.IO.File]::ReadAllText((Join-Path $dir 'plugin.csx'), [System.Text.Encoding]::UTF8)
$manifest = [ordered]@{
    Format = 1; Name = $meta.Name; Key = $meta.Key; Version = $meta.Version
    Description = $meta.Description; Code = $code; Mapping = ''
} | ConvertTo-Json -Depth 3
if (-not $Out) {
    $dist = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $Out = Join-Path $dist "$Key-$($meta.Version).zip"
}
if (Test-Path $Out) { Remove-Item $Out }
$zip = [System.IO.Compression.ZipFile]::Open($Out, 'Create')
try {
    $e = $zip.CreateEntry('plugin.json')
    $w = New-Object System.IO.StreamWriter($e.Open(), (New-Object System.Text.UTF8Encoding($false)))
    $w.Write($manifest); $w.Dispose()
} finally { $zip.Dispose() }
Get-Item $Out | Select-Object FullName, Length
