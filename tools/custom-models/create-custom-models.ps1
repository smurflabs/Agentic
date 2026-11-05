<#
.SYNOPSIS
  Create (or update) the Axiom, Vesper and Zenith custom router models on a running
  Agentic instance, from axiom-vesper-zenith.json next to this script.

.DESCRIPTION
  Each model is POSTed to /api/custom-models (admin only). If a model with the same
  id already exists, the script retries with PUT so re-running is safe.

  Auth uses a bearer API key. Create one in the web app: user menu -> API key
  (the desktop app keeps its own key encrypted for your Windows account, so it is
  not reusable here). The key is read from the -Key argument or the AGENTIC_ADMIN_KEY
  environment variable and is never written to disk or printed.

.EXAMPLE
  .\create-custom-models.ps1 -Server http://localhost:8022 -Key agk_xxx

.EXAMPLE
  $env:AGENTIC_ADMIN_KEY = 'agk_xxx'
  .\create-custom-models.ps1 -Server http://localhost:8022
#>
[CmdletBinding()]
param(
  [string]$Server = 'http://localhost:8022',
  [string]$Key = $env:AGENTIC_ADMIN_KEY,
  [string]$File = (Join-Path $PSScriptRoot 'axiom-vesper-zenith.json')
)

if ([string]::IsNullOrWhiteSpace($Key)) {
  Write-Error "No API key. Pass -Key agk_... or set `$env:AGENTIC_ADMIN_KEY. Create one in the web app: user menu -> API key."
  exit 1
}
if (-not (Test-Path $File)) { Write-Error "Model file not found: $File"; exit 1 }

$Server = $Server.TrimEnd('/')
$models = Get-Content -Raw -Path $File | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $Key" }

foreach ($m in $models) {
  $body = ($m | ConvertTo-Json -Depth 12)
  $id = $m.id
  $url = "$Server/api/custom-models"
  try {
    $r = Invoke-WebRequest -Uri $url -Method POST -Headers $headers -ContentType 'application/json' -Body $body -UseBasicParsing
    Write-Host ("created  {0}  ({1})" -f $id, $m.name) -ForegroundColor Green
  } catch {
    $code = $null; try { $code = [int]$_.Exception.Response.StatusCode } catch {}
    if ($code -eq 400) {
      # Likely already exists -> update in place.
      $put = "$Server/api/custom-models/$id"
      try {
        Invoke-WebRequest -Uri $put -Method PUT -Headers $headers -ContentType 'application/json' -Body $body -UseBasicParsing | Out-Null
        Write-Host ("updated  {0}  ({1})" -f $id, $m.name) -ForegroundColor Yellow
      } catch {
        $msg = try { ($_.ErrorDetails.Message) } catch { $_.Exception.Message }
        Write-Host ("FAILED   {0}: {1}" -f $id, $msg) -ForegroundColor Red
      }
    } elseif ($code -eq 401 -or $code -eq 403) {
      Write-Error "Auth rejected ($code). The key must belong to an admin account."; exit 1
    } else {
      $msg = try { ($_.ErrorDetails.Message) } catch { $_.Exception.Message }
      Write-Host ("FAILED   {0}: {1}" -f $id, $msg) -ForegroundColor Red
    }
  }
}

Write-Host ""
Write-Host "Done. In Chat, pick a model named Axiom, Vesper or Zenith. Assign one to an agent on the agent's model settings." -ForegroundColor Cyan
