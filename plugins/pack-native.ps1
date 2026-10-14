# Packt ein NATIVES MatCMS-Plugin (kompilierte DLLs) in ein Plugin-Bundle -- plugin.json in der Form, die
# PluginPackager.Export fuer native Plugins schreibt (Format 2, Kind "native", alle Felder als Strings), die Dateien
# unter src/, optional assets/. Das Gegenstueck fuer Skript-Plugins ist pack.ps1.
#
#   ./pack-native.ps1 -Path C:\dev\DemoShop                  Projektordner (mit .csproj): wird mit "dotnet publish" gebaut
#   ./pack-native.ps1 -Path C:\dev\DemoShop\publish          fertiger Publish-/Ausgabeordner: wird so genommen, wie er ist
#   ./pack-native.ps1 -Path ... -Out x.zip                   -> plugins/dist/<key>-<version>.zip, wenn -Out fehlt
#
# Key, Name, Version, Description stehen in meta.json (im Projektordner, oder -Meta <Datei>) und lassen sich mit den
# gleichnamigen Parametern ueberschreiben oder ersetzen. Die Hauptdatei (Entry) wird erkannt: die eine DLL im Ordner, die
# MatCMS.Plugin.Abstractions referenziert; -Entry erzwingt sie. Api = Hauptversion der Abstractions, gegen die die
# Hauptdatei gebaut wurde (aus ihren Assembly-Verweisen gelesen, nicht geraten).
#
# Das Bundle ist ein ZIP wie jedes andere: im Admin unter Plugins importieren (oder per Store/API). Es landet deaktiviert und
# laeuft erst, wenn der Betreiber "Native Plugins erlauben" eingeschaltet hat -- nie durch den Import allein.
#
# Zip-Grenzen = src/MatCMS.Shared/PluginBundle.cs (der Import prueft dieselben, hier bricht es frueher und mit Klartext ab).
# Nur ASCII in diesem Skript: Windows PowerShell 5.1 liest eine Datei ohne BOM als ANSI.
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$Meta, [string]$Key, [string]$Name, [string]$Version, [string]$Description,
    [string]$Entry, [string]$Assets, [string]$Out,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# ---- Konstanten, gespiegelt aus PluginBundle.cs --------------------------------------------------------------------
$ApiAssembly      = 'MatCMS.Plugin.Abstractions'
$MaxSourceFile    = 25MB
$MaxSourceTotal   = 60MB
$MaxSourceFiles   = 300
$SourceExt        = @('.dll', '.json', '.txt', '.so')
$MaxAssetFile     = 5MB
$MaxAssetTotal    = 25MB
$MaxAssetFiles    = 200
$AssetExt         = @('.js', '.mjs', '.css', '.json', '.map', '.woff', '.woff2', '.ttf', '.eot', '.png', '.jpg', '.jpeg', '.gif', '.webp')
# PluginBundle.NativeGuardCode: what an OLDER MatCMS shows instead of silently importing an empty plugin. The text has an
# em dash, so it is built from its code point (see the ASCII note above); PackNativeTests compares it with the constant.
$GuardCode = 'throw new System.NotSupportedException("Dies ist ein natives Plugin. Es braucht ein MatCMS mit nativen Plugins ' +
             [string][char]0x2014 + ' bitte MatCMS aktualisieren.");'

$AbstractionsCache = @{}
function Get-AbstractionsVersion([string]$file) {
    # Asked once per file: a second ReflectionOnlyLoad of the same assembly identity throws.
    if (-not $AbstractionsCache.ContainsKey($file)) { $AbstractionsCache[$file] = Read-AbstractionsVersion $file }
    return $AbstractionsCache[$file]
}

function Read-AbstractionsVersion([string]$file) {
    # The version of MatCMS.Plugin.Abstractions that a managed assembly references, or $null (not managed / no reference).
    try {
        if ($PSVersionTable.PSEdition -eq 'Core') {
            $stream = [System.IO.File]::OpenRead($file)
            $pe = New-Object System.Reflection.PortableExecutable.PEReader($stream)
            try {
                if (-not $pe.HasMetadata) { return $null }
                $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                foreach ($h in $md.AssemblyReferences) {
                    $r = $md.GetAssemblyReference($h)
                    if ($md.GetString($r.Name) -eq $ApiAssembly) { return $r.Version }
                }
            } finally { $pe.Dispose() }
            return $null
        }
        # Windows PowerShell: read the metadata without running or locking anything.
        $asm = [System.Reflection.Assembly]::ReflectionOnlyLoad([System.IO.File]::ReadAllBytes($file))
        foreach ($r in $asm.GetReferencedAssemblies()) { if ($r.Name -eq $ApiAssembly) { return $r.Version } }
        return $null
    } catch { return $null }
}

function Test-SourcePath([string]$rel) {
    # The rule of PluginBundle.NormalizeSourcePath for a path that already exists on disk: no hidden or odd names.
    foreach ($seg in $rel.Split('/')) {
        if ($seg.StartsWith('.')) { return $false }
        foreach ($ch in $seg.ToCharArray()) {
            if ([char]::IsControl($ch) -or ':*?"<>|'.IndexOf($ch) -ge 0) { return $false }
        }
    }
    return $true
}

function Get-Files([string]$root) {
    $root = $root.TrimEnd('\', '/')
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        [pscustomobject]@{ Full = $f.FullName; Rel = $f.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/'); Length = $f.Length }
    }
}

$scratch = $null
try {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Ordner nicht gefunden: $Path" }
    $Path = (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\', '/')

    # ---- 1) Was wird gepackt: ein Projektordner wird veroeffentlicht, ein Publish-Ordner genommen --------------------
    $projects = @(Get-ChildItem -LiteralPath $Path -Filter '*.csproj' -File)
    if ($projects.Count -gt 1) { throw "Im Ordner liegen mehrere .csproj ($($projects.Name -join ', ')) -- bitte den Projektordner einzeln angeben." }
    if ($projects.Count -eq 1) {
        $scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('matcms-pack-' + [guid]::NewGuid().ToString('N'))
        Write-Host "Baue $($projects[0].Name) ($Configuration) ..."
        # Quiet, but not silent: a failing build must show why.
        # (Windows PowerShell 5.1 turns anything a native tool writes to stderr into a terminating error under 'Stop'.)
        $ErrorActionPreference = 'Continue'
        $log = & dotnet publish $projects[0].FullName -c $Configuration -o $scratch --nologo -v q 2>&1
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) { throw ("dotnet publish ist fehlgeschlagen (Exitcode $LASTEXITCODE):`n" + ($log -join "`n")) }
        $srcRoot = $scratch
    } else {
        $srcRoot = $Path
    }

    # ---- 2) Metadaten -------------------------------------------------------------------------------------------------
    if (-not $Meta) { $candidate = Join-Path $Path 'meta.json'; if (Test-Path -LiteralPath $candidate) { $Meta = $candidate } }
    $m = $null
    if ($Meta) {
        if (-not (Test-Path -LiteralPath $Meta)) { throw "meta.json nicht gefunden: $Meta" }
        $m = Get-Content -LiteralPath $Meta -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    function Pick([string]$param, [string]$field) {
        if ($param) { return $param }
        if ($m -and $m.PSObject.Properties[$field] -and $m.$field) { return [string]$m.$field }
        return ''
    }
    $Key = (Pick $Key 'Key').Trim(); $Name = (Pick $Name 'Name').Trim(); $Version = (Pick $Version 'Version').Trim()
    $Description = (Pick $Description 'Description').Trim(); $Entry = (Pick $Entry 'Entry').Trim()
    foreach ($req in @(@('Key', $Key), @('Name', $Name), @('Version', $Version))) {
        if (-not $req[1]) { throw "$($req[0]) fehlt: weder -$($req[0]) noch meta.json nennt ihn." }
    }
    # The import slugifies the key; a key that would change under that is a different identity than the author meant.
    if ($Key -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { throw "Key '$Key' ist ungueltig: nur Kleinbuchstaben, Ziffern und '-' (z. B. demo-shop)." }
    if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]*$') { throw "Version '$Version' ist ungueltig: Buchstaben, Ziffern und . _ + - (z. B. 1.0.0)." }

    # ---- 3) Dateien unter src/ ----------------------------------------------------------------------------------------
    $assetsDir = if ($Assets) { $Assets } else { Join-Path $Path 'assets' }
    $assetsHere = Test-Path -LiteralPath $assetsDir -PathType Container
    if ($Assets -and -not $assetsHere) { throw "Assets-Ordner nicht gefunden: $Assets" }

    $src = New-Object System.Collections.Generic.List[object]
    $skipped = New-Object System.Collections.Generic.List[string]
    foreach ($f in Get-Files $srcRoot) {
        # The folder "assets" next to the project (or inside a publish folder) is the plugin's static files, not src/.
        if ($srcRoot -eq $Path -and $f.Rel -like 'assets/*') { continue }
        if ($f.Rel -ieq 'meta.json') { continue }
        $leaf = [System.IO.Path]::GetFileName($f.Rel)
        $ext = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
        if ($leaf -ieq "$ApiAssembly.dll") {
            throw "Der Ordner enthaelt eine eigene Kopie von $ApiAssembly.dll ($($f.Rel)). Sie darf nicht ins Bundle: der Host laedt die Plugin-API selbst, " +
                  "eine zweite Kopie liesse das Plugin ein IPlugin implementieren, das der Host nicht wiedererkennt. " +
                  "Referenz mit <ExcludeAssets>runtime</ExcludeAssets> (PackageReference) bzw. <Private>false</Private> (ProjectReference) setzen und neu bauen."
        }
        if ($leaf -like '*.runtimeconfig*.json') { $skipped.Add($f.Rel + ' (gilt nur fuer eine eigene Anwendung)'); continue }
        if ($SourceExt -notcontains $ext) { $skipped.Add($f.Rel); continue }
        if (-not (Test-SourcePath $f.Rel)) { throw "Pfad '$($f.Rel)' ist in einem Bundle nicht zulaessig (versteckter Name oder unzulaessiges Zeichen)." }
        if ($f.Length -gt $MaxSourceFile) { throw "'$($f.Rel)' ist zu gross: $([math]::Round($f.Length / 1MB, 1)) MB, erlaubt sind $($MaxSourceFile / 1MB) MB je Datei." }
        $src.Add($f)
    }
    if ($src.Count -gt $MaxSourceFiles) { throw "Zu viele Dateien: $($src.Count), erlaubt sind $MaxSourceFiles unter src/." }
    $srcTotal = ($src | Measure-Object Length -Sum).Sum
    if ($srcTotal -gt $MaxSourceTotal) { throw "src/ ist zusammen zu gross: $([math]::Round($srcTotal / 1MB, 1)) MB, erlaubt sind $($MaxSourceTotal / 1MB) MB." }

    # ---- 4) Hauptdatei und Api ----------------------------------------------------------------------------------------
    $top = @($src | Where-Object { $_.Rel -notlike '*/*' })
    if ($Entry) {
        $entryFile = $top | Where-Object { $_.Rel -ieq $Entry } | Select-Object -First 1
        if (-not $entryFile) { throw "Die Hauptdatei '$Entry' fehlt im Ordner (sie muss direkt in der obersten Ebene liegen)." }
    } else {
        $dlls = @($top | Where-Object { $_.Rel -like '*.dll' })
        if ($dlls.Count -eq 0) { throw "Die Hauptdatei fehlt: im Ordner liegt keine .dll (Projekt gebaut? Publish-Ordner angegeben?)." }
        $withApi = @($dlls | Where-Object { Get-AbstractionsVersion $_.Full })
        if ($withApi.Count -eq 0) {
            throw "Die Api-Version ist nicht bestimmbar: keine der DLLs ($(($dlls | ForEach-Object Rel) -join ', ')) verweist auf $ApiAssembly. " +
                  "Ein Plugin muss gegen $ApiAssembly gebaut sein (PackageReference mit ExcludeAssets=runtime)."
        }
        if ($withApi.Count -gt 1) { throw "Mehrere DLLs verweisen auf $ApiAssembly ($(($withApi | ForEach-Object Rel) -join ', ')) -- die Hauptdatei bitte mit -Entry nennen." }
        $entryFile = $withApi[0]
    }
    if ($entryFile.Rel -notlike '*.dll') { throw "Die Hauptdatei '$($entryFile.Rel)' ist keine DLL." }
    $apiVersion = Get-AbstractionsVersion $entryFile.Full
    if (-not $apiVersion) {
        throw "Die Api-Version ist nicht bestimmbar: '$($entryFile.Rel)' verweist nicht auf $ApiAssembly (oder ist keine verwaltete DLL)."
    }
    $api = $apiVersion.Major
    $sha = (Get-FileHash -LiteralPath $entryFile.Full -Algorithm SHA256).Hash.ToLowerInvariant()   # = NativePluginHost.HashOf

    # ---- 5) assets/ (optional, flach -- der Import legt sie unter dem Dateinamen ab) -----------------------------------
    $assetFiles = New-Object System.Collections.Generic.List[object]
    if ($assetsHere) {
        $seen = @{}
        foreach ($f in Get-Files $assetsDir) {
            $leaf = [System.IO.Path]::GetFileName($f.Rel)
            $ext = [System.IO.Path]::GetExtension($leaf).ToLowerInvariant()
            if ($AssetExt -notcontains $ext) { $skipped.Add('assets/' + $f.Rel + ' (Dateityp nicht erlaubt)'); continue }
            if ($f.Length -le 0) { $skipped.Add('assets/' + $f.Rel + ' (leer)'); continue }
            if ($f.Length -gt $MaxAssetFile) { throw "Asset '$($f.Rel)' ist zu gross: $([math]::Round($f.Length / 1MB, 1)) MB, erlaubt sind $($MaxAssetFile / 1MB) MB je Datei." }
            if ($seen.ContainsKey($leaf.ToLowerInvariant())) { throw "Zwei Assets heissen '$leaf' -- im Bundle liegen Assets flach, die Namen muessen eindeutig sein." }
            $seen[$leaf.ToLowerInvariant()] = $true
            $assetFiles.Add([pscustomobject]@{ Full = $f.Full; Rel = $leaf; Length = $f.Length })
        }
        if ($assetFiles.Count -gt $MaxAssetFiles) { throw "Zu viele Assets: $($assetFiles.Count), erlaubt sind $MaxAssetFiles." }
        $assetTotal = ($assetFiles | Measure-Object Length -Sum).Sum
        if ($assetTotal -gt $MaxAssetTotal) { throw "assets/ ist zusammen zu gross: $([math]::Round($assetTotal / 1MB, 1)) MB, erlaubt sind $($MaxAssetTotal / 1MB) MB." }
    }

    # ---- 6) plugin.json + ZIP -----------------------------------------------------------------------------------------
    $manifest = [ordered]@{
        Format = 2; Name = $Name; Key = $Key; Version = $Version; Description = $Description
        Code = $GuardCode; Mapping = ''
        Kind = 'native'; Api = [string]$api; Entry = $entryFile.Rel; Sha256 = $sha
    } | ConvertTo-Json -Depth 3

    if (-not $Out) {
        $dist = Join-Path $PSScriptRoot 'dist'
        New-Item -ItemType Directory -Force $dist | Out-Null
        $Out = Join-Path $dist "$Key-$Version.zip"
    }
    $Out = [System.IO.Path]::GetFullPath($Out)
    New-Item -ItemType Directory -Force ([System.IO.Path]::GetDirectoryName($Out)) | Out-Null
    if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out }
    $zip = [System.IO.Compression.ZipFile]::Open($Out, 'Create')
    try {
        $e = $zip.CreateEntry('plugin.json')
        $w = New-Object System.IO.StreamWriter($e.Open(), (New-Object System.Text.UTF8Encoding($false)))
        $w.Write($manifest); $w.Dispose()
        foreach ($f in ($src | Sort-Object Rel)) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.Full, 'src/' + $f.Rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
        foreach ($f in ($assetFiles | Sort-Object Rel)) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.Full, 'assets/' + $f.Rel, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } catch {
        $zip.Dispose(); $zip = $null
        if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out }
        throw
    } finally { if ($zip) { $zip.Dispose() } }

    Write-Host ("Plugin {0} {1} (Api {2}), Hauptdatei {3}, {4} Datei(en) in src/, {5} Asset(s)" -f $Key, $Version, $api, $entryFile.Rel, $src.Count, $assetFiles.Count)
    Write-Host "Sha256 der Hauptdatei: $sha"
    foreach ($s in $skipped) { Write-Host "  uebersprungen: $s" }
    Write-Host "Import: Admin -> Plugins -> Importieren. Das Plugin laeuft erst, wenn 'Native Plugins erlauben' eingeschaltet ist."
    Write-Host ("Bundle: {0} ({1} Bytes)" -f $Out, (Get-Item -LiteralPath $Out).Length)
} catch {
    [Console]::Error.WriteLine('FEHLER: ' + $_.Exception.Message)
    exit 1
} finally {
    if ($scratch -and (Test-Path -LiteralPath $scratch)) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
