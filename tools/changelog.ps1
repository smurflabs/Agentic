# Adds a release to the apps' changelog (updates\changelog.json), which the Apps & updates page shows per app.
# Dot-source it: . "$PSScriptRoot\changelog.ps1"; Add-ChangelogEntry -UpdatesDir ... -App cli -Version 2.3.18 -Features ...
# An entry for the same app and version is replaced, so publishing a version again updates its notes.

function Add-ChangelogEntry {
    param(
        [Parameter(Mandatory = $true)][string]$UpdatesDir,
        [Parameter(Mandatory = $true)][ValidateSet('cli', 'desktop', 'vscode', 'server')][string]$App,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$Title,
        [string[]]$Features,
        [string[]]$Fixes,
        [string]$Notes,
        [string]$Build
    )
    if (-not (Test-Path $UpdatesDir)) { New-Item -ItemType Directory -Force $UpdatesDir | Out-Null }
    $file = Join-Path $UpdatesDir 'changelog.json'
    $entries = New-Object System.Collections.ArrayList
    if (Test-Path $file) {
        # A file that does not parse stops the publish here rather than being replaced.
        $existing = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($e in @($existing.entries)) {
            if ($null -eq $e) { continue }
            if ($e.app -eq $App -and $e.version -eq $Version) { continue }
            [void]$entries.Add($e)
        }
    }
    $entry = [ordered]@{ app = $App; version = $Version; date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    if ($Title) { $entry.title = $Title }
    $entry.features = @($Features | Where-Object { $_ })
    $entry.fixes = @($Fixes | Where-Object { $_ })
    if ($Notes) { $entry.notes = $Notes }
    if ($Build) { $entry.build = $Build }
    [void]$entries.Add([pscustomobject]$entry)
    $json = ConvertTo-Json -InputObject ([ordered]@{ entries = $entries.ToArray() }) -Depth 8
    $partial = "$file.partial"
    [IO.File]::WriteAllText($partial, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $partial -Destination $file -Force
}
