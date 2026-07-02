$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$port = 8085

Set-Location $root

Write-Host "Starting local static server from: $root"
Write-Host "Open: http://localhost:$port/interactive-test/index.html"
Write-Host "Proxy: enabled at /__proxy to bypass browser CORS limitations"

if (Get-Command python -ErrorAction SilentlyContinue) {
    python .\interactive-test\interactive-proxy-server.py --port $port --root $root
    exit $LASTEXITCODE
}

if (Get-Command py -ErrorAction SilentlyContinue) {
    py .\interactive-test\interactive-proxy-server.py --port $port --root $root
    exit $LASTEXITCODE
}

if (Get-Command npx -ErrorAction SilentlyContinue) {
    npx --yes http-server -p $port
    exit $LASTEXITCODE
}

Write-Error "Python is required for proxy mode. Install Python and rerun this script."
