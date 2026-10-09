<#
.SYNOPSIS  One-time setup for street routing: download roads, build the OSRM graph, start the server.
.DESCRIPTION
  1. Downloads drivable roads for lower/mid Manhattan + nearby Brooklyn/Jersey City from Overpass (~17 MB) into data/area.osm.
  2. Runs osrm-extract / osrm-partition / osrm-customize in Docker (a few seconds for an area this size).
  3. Starts the routing server on http://localhost:5000 and checks it with a sample route.
  Re-running is safe: finished steps are skipped (use -Rebuild to redo them).
.EXAMPLE   ./routing/setup.ps1
#>
param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$image = 'ghcr.io/project-osrm/osrm-backend:latest'
$data = Join-Path $PSScriptRoot 'data'
New-Item -ItemType Directory -Force $data | Out-Null

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Step 'Checking Docker'
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker is not running. Start Docker Desktop, wait until it says "Engine running", then re-run this script.' }

if ($Rebuild) { Get-ChildItem $data -Filter 'area.osrm*' | Remove-Item -Force }

if (-not (Test-Path "$data/area.osm") -or $Rebuild) {
    Step 'Downloading road network from Overpass (bbox in overpass-query.txt)'
    for ($try = 1; $try -le 4; $try++) {
        # curl.exe (not Invoke-WebRequest): Windows PowerShell 5.1 negotiates old TLS settings that some hosts reject.
        curl.exe -sS --compressed -m 280 -A 'logistics-dispatch-demo/1.0 (local development)' -H 'Accept: */*' `
            -X POST --data-urlencode "data@overpass-query.txt" https://overpass-api.de/api/interpreter -o "$data/area.osm"
        if ((Get-Content "$data/area.osm" -TotalCount 3) -match '<osm') { break }
        Write-Host "   Overpass busy or refused the request (try $try/4); retrying in 15 s..." -ForegroundColor Yellow
        Start-Sleep 15
    }
    if (-not ((Get-Content "$data/area.osm" -TotalCount 3) -match '<osm')) { throw 'Could not download the road extract from Overpass. Try again in a few minutes.' }
    "{0:N1} MB downloaded" -f ((Get-Item "$data/area.osm").Length / 1MB) | Write-Host
}

if (-not (Test-Path "$data/area.osrm.mldgr")) {
    Step 'Preparing the routing graph (extract -> partition -> customize)'
    docker run --rm -v "${data}:/data" $image osrm-extract   -p /opt/car.lua /data/area.osm
    if ($LASTEXITCODE -ne 0) { throw 'osrm-extract failed' }
    docker run --rm -v "${data}:/data" $image osrm-partition /data/area.osrm
    if ($LASTEXITCODE -ne 0) { throw 'osrm-partition failed' }
    docker run --rm -v "${data}:/data" $image osrm-customize /data/area.osrm
    if ($LASTEXITCODE -ne 0) { throw 'osrm-customize failed' }
}

Step 'Starting the routing server'
docker compose up -d
if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed' }

Step 'Checking it answers'
$url = 'http://localhost:5000/route/v1/driving/-74.006,40.7128;-73.986,40.748?overview=false'
for ($i = 0; $i -lt 20; $i++) {
    try {
        $r = Invoke-RestMethod $url -TimeoutSec 3
        if ($r.code -eq 'Ok') { Write-Host ("OSRM is up: sample route {0:N0} m, {1:N0} s drive time. Street routing is ready." -f $r.routes[0].distance, $r.routes[0].duration) -ForegroundColor Green; exit 0 }
    } catch { Start-Sleep 1 }
}
throw 'OSRM did not answer on http://localhost:5000 - check `docker logs dispatch-osrm`.'
