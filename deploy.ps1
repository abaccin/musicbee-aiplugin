<#
.SYNOPSIS
    Builds the AI Library Search plugin and deploys it to MusicBee.

.DESCRIPTION
    Builds MusicBee.AI.Search in the chosen configuration, then copies
    mb_AISearch.dll into the MusicBee Plugins folder. All managed
    dependencies are embedded into mb_AISearch.dll by Costura/Fody, and
    the new vector store has no native dependencies, so a single DLL is
    all that needs to be deployed.

    MusicBee must be closed while deploying — it locks loaded plugin DLLs.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER MusicBeePath
    Path to the MusicBee install root (the folder containing MusicBee.exe).
    If not supplied, the script probes common install locations and the
    portable %LOCALAPPDATA%\MusicBee folder.

.PARAMETER NoBuild
    Skip the dotnet build step and just copy whatever is already in bin\.

.PARAMETER Force
    If MusicBee is running, attempt to stop it before deploying.

.PARAMETER NoStart
    Do not launch MusicBee after deployment. By default the script starts it.

.PARAMETER NoToken
    Skip the GitHub CLI token seeding step. By default the script tries to
    populate settings.json with a token from 'gh auth token' if no token is
    already configured.

.EXAMPLE
    .\deploy.ps1
    .\deploy.ps1 -Configuration Debug
    .\deploy.ps1 -MusicBeePath 'D:\Apps\MusicBee' -Force
    .\deploy.ps1 -NoStart
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$MusicBeePath,
    [switch]$NoBuild,
    [switch]$Force,
    [switch]$NoStart,
    [switch]$NoToken
)

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $repoRoot 'MusicBee.AI.Search'
$projectFile = Join-Path $projectDir 'MusicBee.AI.Search.csproj'
$binDir     = Join-Path $projectDir "bin\$Configuration\net48"

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Invoke-Elevated {
    param([string[]]$ForwardArgs)
    $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$($MyInvocation.PSCommandPath)`"") + $ForwardArgs
    Write-Host "Re-launching deploy.ps1 elevated..." -ForegroundColor Yellow
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
}

# Managed assemblies that must be deployed alongside mb_AISearch.dll.
# Everything else is embedded inside mb_AISearch.dll by Costura/Fody, so leaving
# loose copies in Plugins\ would cause them to win over the embedded versions.
# The vector store is now a pure-managed binary file (no SQLite, no native deps).
$managedDeploy = @(
    'mb_AISearch.dll'
)

# Loose files left behind by older deploys that should be scrubbed from Plugins\
# even when they're not present in the current build output.
$legacyArtefacts = @(
    'SQLitePCLRaw.batteries_v2.dll',
    'SQLitePCLRaw.core.dll',
    'SQLitePCLRaw.provider.e_sqlite3.dll',
    'SQLitePCLRaw.provider.dynamic_cdecl.dll',
    'SQLitePCLRaw.lib.e_sqlite3.dll',
    'SQLitePCLRaw.nativelibrary.dll',
    'e_sqlite3.dll',
    'vec0.dll'
)

function Resolve-MusicBeePath {
    param([string]$Hint)

    $candidates = @()
    if ($Hint) { $candidates += $Hint }
    $candidates += @(
        (Join-Path ${env:ProgramFiles}        'MusicBee'),
        (Join-Path ${env:ProgramFiles(x86)}   'MusicBee'),
        (Join-Path ${env:LOCALAPPDATA}        'MusicBee'),
        (Join-Path ${env:APPDATA}             'MusicBee')
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'MusicBee.exe'))) { return (Resolve-Path $c).Path }
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c 'Plugins'))) { return (Resolve-Path $c).Path }
    }
    return $null
}

function Stop-MusicBeeIfRunning {
    param([switch]$Force)
    $proc = Get-Process -Name 'MusicBee' -ErrorAction SilentlyContinue
    if (-not $proc) { return }
    if (-not $Force) {
        throw "MusicBee is currently running. Close it first, or re-run with -Force."
    }
    Write-Host "Stopping MusicBee (PID $($proc.Id))..." -ForegroundColor Yellow
    $proc | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 2
}

function Get-GhAuthToken {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { return $null }
    try {
        $tok = (& gh auth token 2>$null) | Out-String
        $tok = $tok.Trim()
        if ([string]::IsNullOrWhiteSpace($tok)) { return $null }
        return $tok
    } catch { return $null }
}

function Test-GhHasModelsScope {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { return $false }
    try {
        $status = (& gh auth status -h github.com 2>&1) | Out-String
        # GitHub Models inference accepts a fine-grained PAT with the
        # "models:read" permission. Classic OAuth scopes don't have a model
        # scope, so a typical 'gh auth login' token will NOT work. The string
        # below is what 'gh auth status' prints when a fine-grained PAT with
        # the models permission is active.
        return ($status -match 'models:read' -or $status -match 'models')
    } catch { return $false }
}

function Set-GitHubToken {
    param([Parameter(Mandatory)] [string]$PluginsDir)

    # MusicBee plugins write data under Setting_GetPersistentStoragePath(), which
    # for a typical install resolves to %AppData%\MusicBee\mb_storage. settings.json
    # lives under <persistent>\musicbee_ai_search\settings.json.
    $candidates = @(
        (Join-Path $env:APPDATA      'MusicBee\mb_storage\musicbee_ai_search\settings.json'),
        (Join-Path $env:LOCALAPPDATA 'MusicBee\mb_storage\musicbee_ai_search\settings.json')
    )
    $settingsPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    # If MusicBee hasn't yet created the file (first install), pre-seed it next to
    # %AppData% so the plugin picks it up on first run.
    if (-not $settingsPath) {
        $settingsPath = $candidates[0]
        $dir = Split-Path -Parent $settingsPath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }

    $existing = $null
    if (Test-Path $settingsPath) {
        try { $existing = Get-Content $settingsPath -Raw | ConvertFrom-Json } catch { $existing = $null }
    }
    if ($existing -and $existing.Token -and -not [string]::IsNullOrWhiteSpace([string]$existing.Token)) {
        Write-Host "GitHub token already set in $settingsPath -- leaving it alone." -ForegroundColor DarkGray
        return
    }

    $token = Get-GhAuthToken
    if (-not $token) {
        Write-Warning "No token in settings and 'gh auth token' is unavailable."
        Write-Warning "Install GitHub CLI (https://cli.github.com), run 'gh auth login', then re-run this script -- or paste a PAT into the plugin's Settings panel."
        return
    }

    if (-not (Test-GhHasModelsScope)) {
        Write-Warning "Your 'gh' CLI token doesn't appear to grant GitHub Models access."
        Write-Warning "GitHub Models inference requires a FINE-GRAINED personal access token"
        Write-Warning "with the 'Models: Read' permission (no classic OAuth scope works)."
        Write-Warning ""
        Write-Warning "Create one here:  https://github.com/settings/personal-access-tokens/new"
        Write-Warning "  -> Account permissions -> Models -> Read-only"
        Write-Warning "Then paste the token into the plugin's Settings panel,"
        Write-Warning "or set it manually in:  $settingsPath"
        Write-Warning ""
        Write-Warning "(Storing the gh CLI token anyway in case it has the right grant; the API"
        Write-Warning " will return 401 if it doesn't.)"
    }

    $obj = if ($existing) { $existing } else {
        [pscustomobject]@{
            Endpoint            = 'https://models.github.ai/inference'
            ChatModel           = 'openai/gpt-4o-mini'
            EmbeddingModel      = 'openai/text-embedding-3-small'
            EmbeddingDimensions = 1536
            Token               = ''
        }
    }
    $obj | Add-Member -NotePropertyName Token -NotePropertyValue $token -Force
    ($obj | ConvertTo-Json -Depth 8) | Set-Content -Path $settingsPath -Encoding utf8
    Write-Host "Seeded GitHub token from gh CLI into $settingsPath" -ForegroundColor Green
}

# 1. Build
if (-not $NoBuild) {
    Write-Host "Building $Configuration..." -ForegroundColor Cyan
    & dotnet build $projectFile -c $Configuration -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
}

if (-not (Test-Path $binDir)) {
    throw "Build output not found: $binDir. Run without -NoBuild first."
}

$dll = Join-Path $binDir 'mb_AISearch.dll'
if (-not (Test-Path $dll)) { throw "mb_AISearch.dll not found in $binDir." }

# 2. Locate MusicBee
$mbRoot = Resolve-MusicBeePath -Hint $MusicBeePath
if (-not $mbRoot) {
    throw "Could not locate MusicBee. Pass -MusicBeePath <folder containing MusicBee.exe>."
}
$pluginsDir = Join-Path $mbRoot 'Plugins'
if (-not (Test-Path $pluginsDir)) { New-Item -ItemType Directory -Path $pluginsDir | Out-Null }

Write-Host "MusicBee root : $mbRoot"      -ForegroundColor Green
Write-Host "Plugins dir   : $pluginsDir"  -ForegroundColor Green

# 2b. If the target is under Program Files, we need elevation.
$needsElevation = $pluginsDir -like "${env:ProgramFiles}*" -or $pluginsDir -like "${env:ProgramFiles(x86)}*"
if ($needsElevation -and -not (Test-IsAdmin)) {
    $forward = @('-Configuration', $Configuration, '-MusicBeePath', $mbRoot)
    if ($NoBuild) { $forward += '-NoBuild' }
    if ($Force)   { $forward += '-Force' }
    if ($NoStart) { $forward += '-NoStart' }
    Invoke-Elevated -ForwardArgs $forward
    return
}

# 3. Make sure MusicBee isn't holding the DLL
Stop-MusicBeeIfRunning -Force:$Force

# 4. Copy plugin DLL + PDB. All dependencies are embedded by Costura/Fody.
Write-Host "Copying managed assemblies..." -ForegroundColor Cyan

# Clean up any stale loose copies left behind by older deploys (these are now
# embedded in mb_AISearch.dll via Costura, and on-disk versions would shadow
# the embedded ones at load time).
$buildDllNames = (Get-ChildItem $binDir -File -Filter *.dll).Name
$staleCandidates = ($buildDllNames + $legacyArtefacts) | Sort-Object -Unique
foreach ($name in $staleCandidates) {
    if ($managedDeploy -contains $name) { continue }
    $stale = Join-Path $pluginsDir $name
    if (Test-Path $stale) {
        try { Remove-Item $stale -Force; Write-Host "  - $name (removed stale)" -ForegroundColor DarkGray }
        catch { Write-Warning "Failed to remove stale $name : $($_.Exception.Message)" }
    }
}

foreach ($name in $managedDeploy) {
    $src = Join-Path $binDir $name
    if (Test-Path $src) {
        Copy-Item $src $pluginsDir -Force
        Write-Host "  + $name"
    } elseif ($name -eq 'mb_AISearch.dll') {
        throw "mb_AISearch.dll missing from $binDir."
    }
}
$pdb = Join-Path $binDir 'mb_AISearch.pdb'
if (Test-Path $pdb) { Copy-Item $pdb $pluginsDir -Force }

# Also clean any leftover runtimes\ folder from older SQLite-based deploys.
$staleRuntimes = Join-Path $pluginsDir 'runtimes'
if (Test-Path $staleRuntimes) {
    try { Remove-Item $staleRuntimes -Recurse -Force; Write-Host "  - runtimes\ (removed stale)" -ForegroundColor DarkGray }
    catch { Write-Warning "Failed to remove stale runtimes\ folder: $($_.Exception.Message)" }
}

Write-Host ""
Write-Host "Deployed to $pluginsDir" -ForegroundColor Green

# 6. Seed settings.json with a token from gh CLI if needed.
if (-not $NoToken) {
    Set-GitHubToken -PluginsDir $pluginsDir
}

# 7. Launch MusicBee unless suppressed.
if ($NoStart) {
    Write-Host "Skipping MusicBee start (-NoStart)." -ForegroundColor Yellow
    Write-Host "Start MusicBee; the AI Library Search panel can be docked from Preferences -> Plugins." -ForegroundColor Green
    return
}

$mbExe = Join-Path $mbRoot 'MusicBee.exe'
if (Test-Path $mbExe) {
    Write-Host "Starting MusicBee..." -ForegroundColor Cyan
    Start-Process -FilePath $mbExe -WorkingDirectory $mbRoot | Out-Null
    Write-Host "Dock the 'AI Library Search' panel from Preferences -> Plugins once MusicBee is up." -ForegroundColor Green
} else {
    Write-Warning "MusicBee.exe not found at $mbExe -- start it manually."
}
