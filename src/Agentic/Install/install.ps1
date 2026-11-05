# Agentic CLI installer for Windows:   irm __SERVER__/install.ps1 | iex
# From Command Prompt:                 powershell -ExecutionPolicy Bypass -c "irm __SERVER__/install.ps1 | iex"
#
# Installs agentic.exe into %LOCALAPPDATA%\Programs\Agentic (it updates itself there), points it at this server and adds
# that folder to PATH, so "agentic" works in every new Command Prompt and PowerShell window. Run as administrator, it
# goes on the system PATH instead of the user PATH.
# Environment options: AGENTIC_INSTALL_DIR = another folder; AGENTIC_NO_MODIFY_PATH = 1 leaves PATH alone.
& {
    $ErrorActionPreference = 'Stop'
    $server = '__SERVER__'
    $builds = @{
__BUILDS__
    }
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    # Windows on ARM runs the x64 build when there is no ARM64 one.
    $key = if ($arch -eq 'ARM64' -and $builds.ContainsKey('arm64')) { 'arm64' } else { 'x64' }
    if (-not $builds.ContainsKey($key)) { Write-Host 'This server has no Agentic CLI build for Windows yet.' -ForegroundColor Red; return }
    $b = $builds[$key]
    $dir = if ($env:AGENTIC_INSTALL_DIR) { $env:AGENTIC_INSTALL_DIR.TrimEnd('\') } else { Join-Path $env:LOCALAPPDATA 'Programs\Agentic' }

    Write-Host "Installing Agentic CLI $($b.version) from $server ..."
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $exe = Join-Path $dir 'agentic.exe'
    $tmp = Join-Path $dir 'agentic.exe.download'
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    # Invoke-WebRequest is many times faster without its progress bar.
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri ($server + $b.url) -OutFile $tmp
    $sha = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -ne $b.sha256) {
        Remove-Item -LiteralPath $tmp -Force
        Write-Host 'The download did not match its SHA-256; nothing was installed.' -ForegroundColor Red
        return
    }
    try { Move-Item -LiteralPath $tmp -Destination $exe -Force }
    catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        Write-Host 'agentic.exe is in use: close every running agentic and run the installer again.' -ForegroundColor Red
        return
    }
    # Point it at this server, unless it already has a config (the key from an earlier install stays).
    $config = Join-Path $dir 'config.json'
    if (-not (Test-Path -LiteralPath $config)) { [IO.File]::WriteAllText($config, "{ `"serverBaseUrl`": `"$server`" }") }

    if ($env:AGENTIC_NO_MODIFY_PATH -ne '1') {
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $scope = if ($admin) { 'Machine' } else { 'User' }
        $regPath = if ($admin) { 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' } else { 'HKCU:\Environment' }
        # The raw value: entries like %USERPROFILE%\bin stay as they are.
        $current = (Get-Item -LiteralPath $regPath).GetValue('Path', '', 'DoNotExpandEnvironmentNames')
        $parts = @($current -split ';' | Where-Object { $_ })
        if ($parts -notcontains $dir) {
            Set-ItemProperty -LiteralPath $regPath -Name Path -Value (($parts + $dir) -join ';') -Type ExpandString
            # Setting a variable through .NET tells Windows the environment changed, so new windows see the new PATH.
            [Environment]::SetEnvironmentVariable('AGENTIC_CLI_DIR', $dir, $scope)
            Write-Host "Added $dir to the $(if ($admin) { 'system' } else { 'user' }) PATH."
        }
        if (($env:Path -split ';') -notcontains $dir) { $env:Path = $env:Path.TrimEnd(';') + ';' + $dir }
    }

    Write-Host ''
    Write-Host "Agentic CLI $($b.version) installed: $exe" -ForegroundColor Green
    Write-Host 'Next, in a new Command Prompt or PowerShell window:'
    Write-Host '  agentic login    sign in with an API key (Account in the web app)'
    Write-Host '  agentic          start a session in the current folder'
}
