# Builds a self-contained, shippable nodriver-FlareSolverr bundle:
# an embeddable CPython + nodriver/aiohttp + nd_service.py. Jackett runs it via
# <OutDir>\python.exe nd_service.py. PyInstaller is deliberately NOT used - freezing
# breaks nodriver's Cloudflare stealth; embeddable Python behaves identically to source.
param(
    [string]$OutDir = "nodriver-flaresolverr",
    [string]$PyVersion = "3.11.9"
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

Write-Host "Downloading embeddable Python $PyVersion..."
$zip = Join-Path $env:TEMP "py-embed.zip"
Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$PyVersion/python-$PyVersion-embed-amd64.zip" -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $OutDir -Force
Remove-Item $zip

# Enable site-packages so pip-installed deps are importable.
Get-ChildItem $OutDir -Filter "python*._pth" | ForEach-Object {
    (Get-Content $_.FullName) -replace '^#\s*import site', 'import site' | Set-Content $_.FullName
    Add-Content $_.FullName "import site"
}

Write-Host "Bootstrapping pip..."
$py = Join-Path $OutDir "python.exe"
$getpip = Join-Path $env:TEMP "get-pip.py"
Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getpip
& $py $getpip --no-warn-script-location
Remove-Item $getpip

Write-Host "Installing nodriver + aiohttp..."
& $py -m pip install --no-warn-script-location -r (Join-Path $here "requirements.txt")

Copy-Item (Join-Path $here "nd_service.py") (Join-Path $OutDir "nd_service.py") -Force
# Ship requirements.txt too, so a system-Python venv can install the same deps at runtime.
Copy-Item (Join-Path $here "requirements.txt") (Join-Path $OutDir "requirements.txt") -Force

# Sanity check
& $py -c "import nodriver, aiohttp; print('nodriver bundle OK')"
Write-Host "nodriver bundle built at $OutDir"
