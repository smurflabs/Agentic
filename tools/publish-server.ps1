<#
.SYNOPSIS
  Builds the Agentic server (Agentic.exe) with a new version and swaps it into the live publish folder.

.DESCRIPTION
  Every publish gets a new version, like the client apps. The new version is:
    -Version when given;
    the project's <Version> (src\Agentic\Agentic.vbproj) when it is higher than the live server's, so a new major or
    minor version is set by hand in the project;
    otherwise the live server's version with the patch number one higher (2.0.0 -> 2.0.1 -> 2.0.2 ...).
  The version is written back into the project, so the repository always says what is live.

  Then, in this order:
    1. dotnet publish to a stage folder (single-file Agentic.exe, win-x64);
    2. the new exe checks publish\appsettings.json (--validate-config); a bad config stops here, nothing is touched;
    3. every Agentic.exe started from publish\Agentic.exe stops (the main process and its MCP children; nothing else);
    4. the old exe and the database are backed up as *.bak-<Tag>;
    5. the new exe is copied in, retrying while Windows still holds the old one (the old one is started again if the
       copy never succeeds);
    6. the server starts and is watched for -StartupSeconds: it must stay up, answer /healthz and log no "Startup failed";
    7. the release goes into the changelog (Apps & updates page, Claude tab).
  publish\appsettings.json is never written: it holds the live configuration and secrets.

.EXAMPLE
  .\tools\publish-server.ps1 -Title "Model Square" -Features "Model Square cards with filters and sorting" -Fixes "Plan bar progress"
  .\tools\publish-server.ps1 -DryRun            # shows the version it would publish, changes nothing
  .\tools\publish-server.ps1 -Version 3.0.0     # an exact version
  .\tools\publish-server.ps1 -Rollback 20261030-1200
  .\tools\publish-server.ps1 -BeforeStart { node .\grant.js .\publish\data\agentic.db }   # a DB edit while it is stopped
#>
param(
    [string]$PublishDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish'),
    [string]$Project = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Agentic\Agentic.vbproj'),
    [string]$Version,
    # Publish with the project's version as it is (no automatic increase).
    [switch]$NoBump,
    [string]$Tag = (Get-Date -Format 'yyyyMMdd-HHmm'),
    [string]$Title,
    [string[]]$Features,
    [string[]]$Fixes,
    [string]$Notes,
    [int]$StartupSeconds = 75,
    # The port in publish\appsettings.json (Server.Port); the shipped config uses 8080.
    [int]$Port = 8080,
    [switch]$DryRun,
    # Puts back the exe (and only the exe) saved as Agentic.exe.bak-<tag>.
    [string]$Rollback,
    # Runs while the server is stopped, after the new exe is in place and before it starts: the moment for database
    # edits or added config keys. The config is checked again after it.
    [scriptblock]$BeforeStart
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'changelog.ps1')

$liveExe = Join-Path $PublishDir 'Agentic.exe'
$config = Join-Path $PublishDir 'appsettings.json'

function ConvertTo-Ver([string]$Text) {
    $m = [regex]::Match([string]$Text, '^\s*v?(\d+)(?:\.(\d+))?(?:\.(\d+))?')
    if (-not $m.Success) { return [version]'0.0.0' }
    $n = { param($g) if ($g.Success) { [int]$g.Value } else { 0 } }
    return [version]::new((& $n $m.Groups[1]), (& $n $m.Groups[2]), (& $n $m.Groups[3]))
}

function Get-ServerProcesses { @(Get-Process -Name Agentic -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $liveExe }) }

function Stop-Server {
    $procs = Get-ServerProcesses
    Write-Host "Stopping $($procs.Count) process(es) started from $liveExe"
    foreach ($p in $procs) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } }
    for ($i = 0; $i -lt 40 -and (Get-ServerProcesses).Count -gt 0; $i++) { Start-Sleep -Milliseconds 500 }
    if ((Get-ServerProcesses).Count -gt 0) { throw 'The server did not stop.' }
}

# Right after the processes end Windows can still hold the exe for a moment: retry the copy.
function Copy-Exe([string]$From) {
    for ($i = 0; $i -lt 40; $i++) {
        try { Copy-Item -LiteralPath $From -Destination $liveExe -Force -ErrorAction Stop; return $true } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}

function Get-LogText([string]$File, [long]$From) {
    if (-not (Test-Path $File)) { return '' }
    $s = [IO.File]::Open($File, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        if ($From -gt $s.Length) { $From = 0 }
        [void]$s.Seek($From, 'Begin')
        return (New-Object IO.StreamReader($s)).ReadToEnd()
    } finally { $s.Dispose() }
}

# Starts the server and watches it until the start has finished (MCP connect and provider check take up to ~40 s).
function Start-Server {
    # What every log file holds now; the watch reads only what the new process adds (also to a file it rolls over to).
    $logs = Join-Path $PublishDir 'logs'
    $sizes = @{}
    foreach ($f in @(Get-ChildItem -Path $logs -Filter 'agentic-*.log' -ErrorAction SilentlyContinue)) { $sizes[$f.FullName] = $f.Length }
    $proc = Start-Process -FilePath $liveExe -WorkingDirectory $PublishDir -WindowStyle Hidden -PassThru
    Write-Host "Started (pid $($proc.Id)); watching it for $StartupSeconds s..."
    $deadline = (Get-Date).AddSeconds($StartupSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $proc.Refresh()
        if ($proc.HasExited) { throw "The server exited during startup (code $($proc.ExitCode)). See $logs." }
        foreach ($f in @(Get-ChildItem -Path $logs -Filter 'agentic-*.log' -ErrorAction SilentlyContinue)) {
            $from = if ($sizes.ContainsKey($f.FullName)) { $sizes[$f.FullName] } else { 0 }
            if ((Get-LogText $f.FullName $from) -match '"Startup failed[^"]*"') { throw "The server logged $($Matches[0]). See $($f.FullName)." }
        }
    }
    try { $health = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 "http://127.0.0.1:$Port/healthz" } catch { throw "The server is running but /healthz does not answer: $($_.Exception.Message)" }
    if ($health.StatusCode -ne 200) { throw "/healthz answered $($health.StatusCode)." }
    Write-Host "Up: /healthz answers and the log shows no startup failure."
}

if ($Rollback) {
    $backup = "$liveExe.bak-$Rollback"
    if (-not (Test-Path $backup)) { throw "No backup $backup." }
    if ($DryRun) { Write-Host "Would put back $backup."; return }
    Stop-Server
    if (-not (Copy-Exe $backup)) { throw 'The exe is still locked; nothing was changed. Start the server again by hand.' }
    Start-Server
    Write-Host "Rolled back to $backup ($((Get-Item $liveExe).VersionInfo.FileVersion)). The database was not touched."
    return
}

# ---- The version
$projectText = [IO.File]::ReadAllText($Project)
$projectVersion = ConvertTo-Ver ([regex]::Match($projectText, '<Version>\s*([^<\s]+)\s*</Version>').Groups[1].Value)
$liveVersion = if (Test-Path $liveExe) { ConvertTo-Ver (Get-Item $liveExe).VersionInfo.FileVersion } else { [version]'0.0.0' }
$next = if ($Version) { ConvertTo-Ver $Version }
        elseif ($NoBump -or $projectVersion -gt $liveVersion) { $projectVersion }
        else { [version]::new($liveVersion.Major, $liveVersion.Minor, $liveVersion.Build + 1) }
$nextText = "$($next.Major).$($next.Minor).$($next.Build)"
Write-Host "Live server $liveVersion, project $projectVersion -> publishing $nextText (backups tagged $Tag)"
if ($DryRun) { return }

if ($projectVersion -ne $next) {
    $bom = [IO.File]::ReadAllBytes($Project)[0] -eq 0xEF
    $text = $projectText -replace '<Version>[^<]*</Version>', "<Version>$nextText</Version>" `
        -replace '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$nextText.0</AssemblyVersion>" `
        -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$nextText.0</FileVersion>"
    [IO.File]::WriteAllText($Project, $text, (New-Object Text.UTF8Encoding($bom)))
    Write-Host "Project version set to $nextText"
}

# ---- Build
$stage = Join-Path ([IO.Path]::GetTempPath()) ('agentic-publish-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Write-Host 'Building...'
$buildLog = & dotnet publish $Project -c Release -r win-x64 -o $stage -nologo 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { Write-Host $buildLog; throw 'dotnet publish failed; the live server was not touched.' }
$staged = Join-Path $stage 'Agentic.exe'
if (-not (Test-Path $staged)) { throw "The build did not produce Agentic.exe in $stage." }
$builtVersion = ConvertTo-Ver (Get-Item $staged).VersionInfo.FileVersion
if ($builtVersion -ne $next) { throw "The build says version $builtVersion, expected $nextText." }

# ---- Check the live config with the new build
$check = & $staged --config $config --validate-config 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    # Only the checker's verdict lines: the config holds secrets.
    ($check -split "`r?`n" | Where-Object { $_ -match '^\s*(error|warning)|Configuration' }) | ForEach-Object { Write-Host $_ }
    throw 'publish\appsettings.json does not pass the new build''s check; the live server was not touched.'
}
Write-Host 'Config check passed.'

# ---- Swap
Stop-Server
Copy-Item -LiteralPath $liveExe -Destination "$liveExe.bak-$Tag" -Force
foreach ($f in 'agentic.db', 'agentic.db-wal', 'agentic.db-shm') {
    $db = Join-Path $PublishDir "data\$f"
    if (Test-Path $db) { Copy-Item -LiteralPath $db -Destination "$db.bak-$Tag" -Force }
}
Write-Host "Backed up the exe and the database as *.bak-$Tag"
if (-not (Copy-Exe $staged)) {
    Write-Warning 'The exe stayed locked; starting the old build again.'
    Start-Server
    throw 'The new build was not installed.'
}
if ($BeforeStart) {
    Write-Host 'Running -BeforeStart while the server is stopped...'
    # Windows PowerShell turns a native program's stderr (e.g. node's warnings) into errors, and 'Stop' would end the
    # hook there: run it with 'Continue' so only its own throws stop it.
    $ErrorActionPreference = 'Continue'
    try { & $BeforeStart }
    catch { throw "-BeforeStart failed: $($_.Exception.Message). The server is STOPPED with the new exe in place: fix it and start it, or -Rollback $Tag." }
    finally { $ErrorActionPreference = 'Stop' }
    $check = & $liveExe --config $config --validate-config 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        ($check -split "`r?`n" | Where-Object { $_ -match '^\s*(error|warning)|Configuration' }) | ForEach-Object { Write-Host $_ }
        throw "After -BeforeStart the config does not pass the check; the server was NOT started. Fix it (or restore a backup) and start it, or roll back: -Rollback $Tag."
    }
}
Start-Server
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# ---- Changelog
$build = ''
try { $build = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 "http://127.0.0.1:$Port/healthz").Headers['X-Agentic-Build'] } catch { }
Add-ChangelogEntry -UpdatesDir (Join-Path $PublishDir 'updates') -App server -Version $nextText -Title $Title -Features $Features -Fixes $Fixes -Notes $Notes -Build $build
Write-Host "Published server $nextText$(if ($build) { " (build $build)" }). Rollback: .\tools\publish-server.ps1 -Rollback $Tag"
