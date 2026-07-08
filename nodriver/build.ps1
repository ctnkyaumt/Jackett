# Ships the nodriver-FlareSolverr assets alongside Jackett: just the solver script and
# its requirements. Python is NOT bundled; the C# NodriverManagerService resolves it at
# runtime (system Python preferred, embeddable Python downloaded on-demand as last resort).
param(
    [string]$OutDir = "nodriver-flaresolverr"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

Copy-Item (Join-Path $here "nd_service.py") (Join-Path $OutDir "nd_service.py") -Force
Copy-Item (Join-Path $here "requirements.txt") (Join-Path $OutDir "requirements.txt") -Force

Write-Host "nodriver solver assets shipped to $OutDir (nd_service.py + requirements.txt)"
