<#
.SYNOPSIS
  Builds the Agentic CLI and the Agentic Desktop app as single-file programs for Windows and Linux and publishes them to the
  server's updates folder, where installed copies find them (GET /api/updates) and update themselves.

.DESCRIPTION
  Layout written (Updates.Directory in appsettings.json, default "updates" next to it):
    updates\cli\agentic.exe                          Windows (win-x64)
    updates\desktop\AgenticClient.exe
    updates\desktop\www\desktop\...                  mirrored into each desktop install's www\desktop
    updates\cli-linux-x64\agentic                    Linux (linux-x64): the same projects built with -r linux-x64
    updates\desktop-linux-x64\AgenticClient
    updates\desktop-linux-x64\www\desktop\...
    updates\<folder>\release.json                    { "notes", "minimumVersion" }; Linux builds also get "version" (the
                                                     server cannot read it from a Linux binary)
  Files are copied under a temporary name and renamed, so a running server never hands out half a file.
  The server can keep running while you publish. Bump <Version> in the project for a new version; a rebuild with the
  same version is offered too (clients compare SHA-256).

.EXAMPLE
  .\tools\publish-clients.ps1
  .\tools\publish-clients.ps1 -Apps cli -Notes "Faster startup"
  .\tools\publish-clients.ps1 -Apps desktop -Platforms linux-x64
  .\tools\publish-clients.ps1 -Apps desktop -Version 0.3.0
  .\tools\publish-clients.ps1 -UpdatesDir D:\Agentic\updates -MinimumVersion 2.1.0 -Apps cli
  .\tools\publish-clients.ps1 -Apps cli -Title "Plan gauge" -Features "Plan progress on the working line" -Fixes "..."

  -Title, -Features and -Fixes go into the changelog (updates\changelog.json, the Apps & updates page) for each app
  published; without them the server records the version with its -Notes when it first sees it.
#>
param(
    [string]$UpdatesDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\updates'),
    [ValidateSet('cli', 'desktop', 'vscode')][string[]]$Apps = @('cli', 'desktop', 'vscode'),
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string[]]$Platforms = @('win-x64', 'linux-x64'),
    # The client repositories are expected next to this one (..\Agentic_CLI, ..\Agentic_Client, ..\Agentic_VSCODE).
    [string]$CliProject = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Agentic_CLI\AgenticCli.vbproj'),
    [string]$DesktopProject = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Agentic_Client\AgenticClient.vbproj'),
    # The VS Code extension's folder (package.json); one .vsix for every platform, version from its package.json.
    [string]$VsCodeProject = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'Agentic_VSCODE'),
    # Node 20 or later for vsce (it needs the File global): the copy in tools\node, else node on PATH.
    [string]$NodeExe = $(if (Test-Path (Join-Path $PSScriptRoot 'node\node.exe')) { Join-Path $PSScriptRoot 'node\node.exe' } else { 'node' }),
    [string]$Notes,
    # Overrides <Version> of the projects for this build (normally you bump it in the .vbproj).
    [string]$Version,
    [string]$MinimumVersion,
    # The changelog entry for this release (each app published gets it).
    [string]$Title,
    [string[]]$Features,
    [string[]]$Fixes,
    # Leave out the desktop app's MSI (built with each Windows desktop build otherwise).
    [switch]$SkipMsi,
    # Only build the desktop MSI from the desktop build already published (no app builds).
    [switch]$MsiOnly
)
$ErrorActionPreference = 'Stop'
# The desktop MSI's upgrade code: never change it, or new MSIs stop replacing installed ones.
$DesktopUpgradeCode = '{5B0E4C1A-6D2F-4E8B-9A71-3C4D2E1F0A93}'
. (Join-Path $PSScriptRoot 'changelog.ps1')

function Copy-Atomic([string]$Source, [string]$Target) {
    $dir = Split-Path $Target -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $partial = "$Target.partial"
    Copy-Item -LiteralPath $Source -Destination $partial -Force
    Move-Item -LiteralPath $partial -Destination $Target -Force
}

# The project's <Version> (or -Version): what the Linux builds' release.json says.
function Get-ProjectVersion([string]$Project) {
    if ($Version) { return $Version }
    $m = [regex]::Match((Get-Content -LiteralPath $Project -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { throw "No <Version> in $Project; pass -Version." }
    return $m.Groups[1].Value
}

function Publish-App([string]$App, [string]$Project, [string]$BaseName, [string[]]$ContentFolders, [string]$Rid) {
    if (-not (Test-Path $Project)) { throw "Project not found: $Project" }
    $windows = $Rid.StartsWith('win')
    $exeName = if ($windows) { "$BaseName.exe" } else { $BaseName }
    $folder = if ($windows) { $App } else { "$App-$Rid" }
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("agentic-publish-$folder-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Write-Host "Building $App for $Rid ($Project)..."
    $extraArgs = @('-r', $Rid)
    if ($Version) { $extraArgs += "-p:Version=$Version" }
    $log = & dotnet publish $Project -c Release -o $stage -nologo @extraArgs 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Host $log; throw "dotnet publish failed for $App ($Rid)." }
    $exe = Join-Path $stage $exeName
    if (-not (Test-Path $exe)) { throw "The publish of $App ($Rid) did not produce $exeName." }
    $extra = Get-ChildItem $stage -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.so' }
    if ($extra) { Write-Warning "$App ($Rid) published loose files ($($extra.Name -join ', ')); it is not single-file." }

    $target = Join-Path $UpdatesDir $folder
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Atomic $exe (Join-Path $target $exeName)

    foreach ($content in $ContentFolders) {
        $src = Join-Path $stage $content
        $dst = Join-Path $target $content
        if (-not (Test-Path $src)) { continue }
        # -Name gives paths relative to the folder (FullName may differ from it, e.g. 8.3 short names in TEMP).
        $wanted = @{}
        foreach ($rel in @(Get-ChildItem -LiteralPath $src -Recurse -File -Name)) {
            $wanted[$rel.ToLowerInvariant()] = $true
            $from = Join-Path $src $rel
            $to = Join-Path $dst $rel
            if ((Test-Path -LiteralPath $to) -and ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash)) { continue }
            Copy-Atomic $from $to
        }
        # Files the new build no longer has go too (clients then delete them as well).
        if (Test-Path -LiteralPath $dst) {
            foreach ($rel in @(Get-ChildItem -LiteralPath $dst -Recurse -File -Name)) {
                if (-not $wanted.ContainsKey($rel.ToLowerInvariant())) { Remove-Item -LiteralPath (Join-Path $dst $rel) -Force }
            }
        }
    }

    $releaseFile = Join-Path $target 'release.json'
    $projectVersion = Get-ProjectVersion $Project
    if ($Notes -or $MinimumVersion -or -not $windows) {
        $release = [ordered]@{}
        if (Test-Path $releaseFile) {
            $old = Get-Content $releaseFile -Raw | ConvertFrom-Json
            foreach ($p in $old.PSObject.Properties) { $release[$p.Name] = $p.Value }
        }
        if ($Notes) { $release['notes'] = $Notes }
        if ($MinimumVersion) { $release['minimumVersion'] = $MinimumVersion }
        # A Linux binary has no version resource the server could read.
        if (-not $windows) { $release['version'] = $projectVersion }
        [IO.File]::WriteAllText($releaseFile, ($release | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    }

    $published = Join-Path $target $exeName
    $shownVersion = if ($windows) { (Get-Item $published).VersionInfo.ProductVersion } else { $projectVersion }
    $sha = (Get-FileHash $published -Algorithm SHA256).Hash.ToLowerInvariant()
    $mb = [Math]::Round((Get-Item $published).Length / 1MB, 1)
    Write-Host ("  {0,-18} {1,-10} {2,7} MB  sha256 {3}" -f $folder, $shownVersion, $mb, $sha.Substring(0, 16))
    if ($App -eq 'desktop' -and $windows -and -not $SkipMsi) { Publish-DesktopMsi $target (Get-ProjectVersion $Project) $Rid }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

# The desktop app's Windows installer: updates\installers\AgenticDesktop-<version>-<x64|arm64>.msi (per-user, into
# %LOCALAPPDATA%\Programs\Agentic Desktop, Start menu and desktop shortcuts), built from the published exe and www folder
# by tools\MsiBuilder (Windows' own msi.dll and makecab; no WiX). Older MSIs of that architecture are removed.
function Publish-DesktopMsi([string]$From, [string]$AppVersion, [string]$Rid) {
    $arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $builder = Join-Path $PSScriptRoot 'MsiBuilder\bin\Release\net10.0\MsiBuilder.exe'
    if (-not (Test-Path $builder)) {
        $log = & dotnet build (Join-Path $PSScriptRoot 'MsiBuilder\MsiBuilder.vbproj') -c Release -nologo 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $log; throw 'MsiBuilder did not build.' }
    }
    $src = Join-Path ([IO.Path]::GetTempPath()) ('agentic-msi-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $src | Out-Null
    try {
        Copy-Item -LiteralPath (Join-Path $From 'AgenticClient.exe') -Destination $src
        if (Test-Path (Join-Path $From 'www')) { Copy-Item -LiteralPath (Join-Path $From 'www') -Destination $src -Recurse }
        $dir = Join-Path $UpdatesDir 'installers'
        New-Item -ItemType Directory -Force $dir | Out-Null
        $name = "AgenticDesktop-$AppVersion-$arch.msi"
        $out = & $builder --source $src --exe AgenticClient.exe --name 'Agentic Desktop' --manufacturer Agentic --version $AppVersion `
            --upgrade-code $DesktopUpgradeCode --arch $arch --out (Join-Path $dir $name) 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $out; throw 'The desktop MSI was not built.' }
        Get-ChildItem $dir -Filter "AgenticDesktop-*-$arch.msi" | Where-Object { $_.Name -ne $name } | Remove-Item -Force
        Write-Host ("  {0,-18} {1,-10} {2,7} MB  {3}" -f 'installer', $AppVersion, [Math]::Round((Get-Item (Join-Path $dir $name)).Length / 1MB, 1), $name)
    } finally { Remove-Item $src -Recurse -Force -ErrorAction SilentlyContinue }
}

# The VS Code extension: one .vsix for every platform in updates\vscode (release.json names it and gives its version).
function Publish-VsCode {
    $packageJson = Join-Path $VsCodeProject 'package.json'
    if (-not (Test-Path $packageJson)) { throw "VS Code extension not found: $VsCodeProject" }
    $extVersion = (Get-Content -LiteralPath $packageJson -Raw | ConvertFrom-Json).version
    Write-Host "Building the VS Code extension $extVersion ($VsCodeProject)..."
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("agentic-publish-vscode-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $stage | Out-Null
    $vsix = Join-Path $stage 'agentic-vscode.vsix'
    Push-Location $VsCodeProject
    try {
        if (-not (Test-Path 'node_modules')) {
            $log = & npm install --no-audit --no-fund 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { Write-Host $log; throw 'npm install failed for the VS Code extension.' }
        }
        $log = & npm run compile 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { Write-Host $log; throw 'The VS Code extension did not compile.' }
        $log = & $NodeExe 'node_modules/@vscode/vsce/vsce' package --no-dependencies --allow-missing-repository --skip-license -o $vsix 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $vsix)) { Write-Host $log; throw "vsce could not package the extension (it needs Node 20 or later: -NodeExe)." }
    } finally { Pop-Location }

    $target = Join-Path $UpdatesDir 'vscode'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Atomic $vsix (Join-Path $target 'agentic-vscode.vsix')
    $releaseFile = Join-Path $target 'release.json'
    $release = [ordered]@{}
    if (Test-Path $releaseFile) { foreach ($p in (Get-Content $releaseFile -Raw | ConvertFrom-Json).PSObject.Properties) { $release[$p.Name] = $p.Value } }
    $release['file'] = 'agentic-vscode.vsix'
    $release['version'] = $extVersion
    if ($Notes) { $release['notes'] = $Notes }
    if ($MinimumVersion) { $release['minimumVersion'] = $MinimumVersion }
    [IO.File]::WriteAllText($releaseFile, ($release | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    $published = Join-Path $target 'agentic-vscode.vsix'
    $sha = (Get-FileHash $published -Algorithm SHA256).Hash.ToLowerInvariant()
    $kb = [Math]::Round((Get-Item $published).Length / 1KB, 1)
    Write-Host ("  {0,-18} {1,-10} {2,7} KB  sha256 {3}" -f 'vscode', $extVersion, $kb, $sha.Substring(0, 16))
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $UpdatesDir | Out-Null
if ($MsiOnly) {
    $published = Join-Path $UpdatesDir 'desktop'
    $v = (Get-Item (Join-Path $published 'AgenticClient.exe')).VersionInfo
    Publish-DesktopMsi $published "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)" 'win-x64'
    return
}
foreach ($rid in $Platforms) {
    if ($Apps -contains 'cli') { Publish-App 'cli' $CliProject 'agentic' @() $rid }
    if ($Apps -contains 'desktop') { Publish-App 'desktop' $DesktopProject 'AgenticClient' @('www') $rid }
}
if ($Apps -contains 'vscode') { Publish-VsCode }
if ($Title -or $Features -or $Fixes) {
    foreach ($app in $Apps) {
        $appVersion = switch ($app) {
            'cli' { Get-ProjectVersion $CliProject }
            'desktop' { Get-ProjectVersion $DesktopProject }
            'vscode' { (Get-Content -LiteralPath (Join-Path $VsCodeProject 'package.json') -Raw | ConvertFrom-Json).version }
        }
        Add-ChangelogEntry -UpdatesDir $UpdatesDir -App $app -Version $appVersion -Title $Title -Features $Features -Fixes $Fixes -Notes $Notes
        Write-Host "  changelog: $app $appVersion"
    }
}
Write-Host "Published to $UpdatesDir. Installed apps pick it up at their next check (Apps & updates in the web app)."
