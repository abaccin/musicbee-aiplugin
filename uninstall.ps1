#requires -Version 5.1
<#
.SYNOPSIS
    Removes the AI Library Search plugin from MusicBee and wipes its data.

.DESCRIPTION
    - Stops MusicBee if running (with -Force).
    - Deletes mb_AISearch.dll / .pdb from MusicBee's Plugins folder.
    - Deletes the plugin's persistent data folder (settings.json, vector store).
    - Leaves MusicBee's own settings / library alone.

    After running this, start MusicBee and -- if there's an empty panel slot
    where the chat used to be -- open View -> Arrange Panels and remove it.

.PARAMETER MusicBeePath
    Path to MusicBee install. Auto-detected if omitted.

.PARAMETER Force
    Stop MusicBee if it is currently running.

.PARAMETER KeepData
    Keep the plugin's persistent data (settings + embeddings).
#>

[CmdletBinding()]
param(
    [string] $MusicBeePath,
    [switch] $Force,
    [switch] $KeepData
)

$ErrorActionPreference = 'Stop'

function Resolve-MusicBeeRoot {
    param([string] $Hint)
    $candidates = @()
    if ($Hint) { $candidates += $Hint }
    $candidates += @(
        'C:\Program Files (x86)\MusicBee',
        'C:\Program Files\MusicBee',
        (Join-Path $env:LOCALAPPDATA 'MusicBee'),
        (Join-Path $env:APPDATA      'MusicBee')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'Plugins'))) { return (Resolve-Path $c).Path }
    }
    return $null
}

$mbRoot = Resolve-MusicBeeRoot $MusicBeePath
if (-not $mbRoot) { throw "Could not find MusicBee install. Pass -MusicBeePath '<dir>'." }
$pluginsDir = Join-Path $mbRoot 'Plugins'
Write-Host "MusicBee root: $mbRoot" -ForegroundColor Cyan

# Stop MusicBee if running.
$proc = Get-Process -Name 'MusicBee' -ErrorAction SilentlyContinue
if ($proc) {
    if (-not $Force) { throw "MusicBee is running. Re-run with -Force." }
    Write-Host "Stopping MusicBee (PID $($proc.Id))..." -ForegroundColor Yellow
    $proc | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 2
}

# Remove plugin binaries.
$removed = @()
foreach ($name in @('mb_AISearch.dll', 'mb_AISearch.pdb')) {
    $p = Join-Path $pluginsDir $name
    if (Test-Path $p) {
        try { Remove-Item $p -Force; $removed += $p }
        catch { Write-Warning "Failed to delete $p : $($_.Exception.Message)" }
    }
}
# Sweep any leftover legacy artefacts from earlier deploys.
foreach ($pattern in @('SQLitePCLRaw.*.dll', 'e_sqlite3.dll', 'vec0.dll',
                       'PresentationFramework*.dll', 'PresentationCore*.dll',
                       'WindowsBase*.dll', 'System.Xaml*.dll')) {
    Get-ChildItem -Path $pluginsDir -Filter $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        try { Remove-Item $_.FullName -Force; $removed += $_.FullName }
        catch { Write-Warning "Failed to delete $($_.FullName): $($_.Exception.Message)" }
    }
}
$staleRuntimes = Join-Path $pluginsDir 'runtimes'
if (Test-Path $staleRuntimes) {
    try { Remove-Item $staleRuntimes -Recurse -Force; $removed += $staleRuntimes }
    catch { Write-Warning "Failed to delete $staleRuntimes : $($_.Exception.Message)" }
}

if ($removed.Count -eq 0) {
    Write-Host "No plugin files found in $pluginsDir" -ForegroundColor DarkGray
} else {
    Write-Host "Removed:" -ForegroundColor Green
    $removed | ForEach-Object { Write-Host "  - $_" -ForegroundColor DarkGray }
}

# Remove persistent data unless asked to keep it.
if (-not $KeepData) {
    $dataCandidates = @(
        (Join-Path $env:APPDATA      'MusicBee\mb_storage\musicbee_ai_search'),
        (Join-Path $env:LOCALAPPDATA 'MusicBee\mb_storage\musicbee_ai_search')
    )
    foreach ($d in $dataCandidates) {
        if (Test-Path $d) {
            try {
                Remove-Item $d -Recurse -Force
                Write-Host "Deleted plugin data: $d" -ForegroundColor Green
            }
            catch { Write-Warning "Failed to delete $d : $($_.Exception.Message)" }
        }
    }
} else {
    Write-Host "Keeping plugin data (-KeepData)." -ForegroundColor Yellow
}

# Strip our <State>...</State> block from MusicBee's settings so the orphan panel
# slot (and any corrupted negative PanelHeight) doesn't keep haunting the layout.
$settingsFile = Join-Path $env:APPDATA 'MusicBee\MusicBee3Settings.ini'
if (Test-Path $settingsFile) {
    try {
        $raw = Get-Content $settingsFile -Raw
        $pattern = '(?s)\s*<State>\s*<Id>[^<]*mb_AISearch\.dll</Id>.*?</State>'
        $hits = [regex]::Matches($raw, $pattern)
        if ($hits.Count -gt 0) {
            $backup = "$settingsFile.bak-aiclean-$(Get-Date -Format yyyyMMdd-HHmmss)"
            Copy-Item $settingsFile $backup -Force
            $cleaned = [regex]::Replace($raw, $pattern, '')
            [System.IO.File]::WriteAllText($settingsFile, $cleaned, [System.Text.Encoding]::UTF8)
            Write-Host "Removed plugin entry from $settingsFile (backup: $backup)" -ForegroundColor Green
        } else {
            Write-Host "No plugin entry found in $settingsFile" -ForegroundColor DarkGray
        }
    } catch {
        Write-Warning "Could not clean $settingsFile : $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "Done. Start MusicBee; if an empty 'AI Library Search' slot remains in the layout," -ForegroundColor Green
Write-Host "open View -> Arrange Panels and drag it out of the layout, then click Save." -ForegroundColor Green
