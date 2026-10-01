<#
.SYNOPSIS
  Local end-to-end test bed: Cove + generation worker(s), extension pre-installed. Workers read videos from Cove over HTTP.

.EXAMPLE
  ./setup.ps1                 # build extension, install it, create sample videos, start, register workers, scan
  ./setup.ps1 -TwoServers     # same, plus worker-2, which Cove dials (distribution / failover testing)
  ./setup.ps1 -Large          # also fetch 1080p Big Buck Bunny + Sintel (~1.5 GB more)
  ./setup.ps1 reinstall       # rebuild the extension zip and hot-swap it into the running Cove
  ./setup.ps1 logs            # follow logs
  ./setup.ps1 down            # stop (keeps data)
  ./setup.ps1 reset           # stop and delete ALL local test data (database, generated files, sample media)
#>
param(
    [ValidateSet("up", "reinstall", "logs", "down", "reset")]
    [string]$Command = "up",
    [Alias("TwoWorkers")][switch]$TwoServers,
    [switch]$CoveFromSource,
    [switch]$SkipBuild,
    [switch]$Large,
    [int]$CovePort = 5073
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path -Parent $here
$data = Join-Path $here ".data"
$extensionId = "com.cove.remote-heavylifter"
$extensionDir = Join-Path $data "cove/config/extensions/$extensionId"
$envFile = Join-Path $here ".env"
# 127.0.0.1, not localhost: .NET tries ::1 first, and Docker Desktop's IPv6 port forwarder accepts
# the connection but never answers, so every request would hang until its timeout.
$cove = "http://127.0.0.1:$CovePort"
$ownerUser = "admin"
$ownerPassword = "adminadmin"
$api = "$cove/api/ext/$extensionId"

function Compose {
    $files = @("-f", (Join-Path $here "docker-compose.yml"))
    if ($CoveFromSource) { $files += @("-f", (Join-Path $here "docker-compose.cove-source.yml")) }
    $profiles = if ($TwoServers) { @("--profile", "multi") } else { @() }
    & docker compose --project-directory $here @files @profiles @args
    if ($LASTEXITCODE) { throw "docker compose $args failed" }
}

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Wait-Http($url, $what, $timeoutSeconds, [switch]$Json) {
    # -Json: an unknown /api path falls through to Cove's SPA and answers 200 with HTML, so require JSON.
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $url -TimeoutSec 5 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200 -and (-not $Json -or "$($response.Headers['Content-Type'])" -like "*json*")) { return }
        } catch { }
        Start-Sleep -Seconds 3
    }
    throw "$what did not come up within $timeoutSeconds s ($url). Check: ./setup.ps1 logs"
}

function Install-Extension {
    if (-not $SkipBuild) {
        Step "Building the extension package"
        & pwsh -NoProfile -File (Join-Path $repo "scripts/package.ps1")
        if ($LASTEXITCODE) { throw "packaging failed" }
    }
    $version = (Get-Content (Join-Path $repo "VERSION") -Raw).Trim()
    $zip = Join-Path $repo "artifacts/remote-heavylifter-$version.zip"
    if (-not (Test-Path $zip)) { throw "$zip not found (run without -SkipBuild)" }

    Step "Installing $([IO.Path]::GetFileName($zip)) into Cove's config/extensions"
    if (Test-Path $extensionDir) { Remove-Item -Recurse -Force $extensionDir }
    New-Item -ItemType Directory -Force $extensionDir | Out-Null
    Expand-Archive -Path $zip -DestinationPath $extensionDir -Force
    return $zip
}

function Invoke-Ffmpeg([string]$media, [string[]]$ffmpegArgs) {
    # Paths in $ffmpegArgs are relative to the media folder; use the host ffmpeg or borrow the worker image's.
    if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
        Push-Location $media
        try { & ffmpeg @ffmpegArgs } finally { Pop-Location }
    } else {
        & docker run --rm --user root --entrypoint /usr/local/bin/ffmpeg -v "${media}:/work" -w /work remote-heavylifter-worker:local @ffmpegArgs
    }
    if ($LASTEXITCODE) { throw "ffmpeg failed: $ffmpegArgs" }
}

function Get-Download([string]$url) {
    # Downloads are cached in local-test/.downloads so `reset` does not fetch them again.
    $cache = Join-Path $here ".downloads"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $file = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$url).AbsolutePath))
    if (-not (Test-Path $file)) {
        Write-Host "    downloading $url"
        & curl.exe -fL --retry 3 -o "$file.part" $url
        if ($LASTEXITCODE) { throw "download failed: $url" }
        Move-Item "$file.part" $file
    }
    if ($file.EndsWith(".zip")) {
        $unpacked = Join-Path $cache ([IO.Path]::GetFileNameWithoutExtension($file))
        if (-not (Test-Path $unpacked)) {
            $tmp = Join-Path $cache "unzip-tmp"
            if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
            Expand-Archive $file -DestinationPath $tmp
            Get-ChildItem $tmp -Recurse -File | Sort-Object Length -Descending | Select-Object -First 1 | Move-Item -Destination $unpacked
            Remove-Item -Recurse -Force $tmp
        }
        return $unpacked
    }
    return $file
}

function New-SampleMedia {
    $media = Join-Path $data "media"
    if (Get-ChildItem $media -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
        Step "Sample media already present in $media"
        return
    }
    Step "Fetching sample videos (Blender open movies, CC-BY) into $media"
    # target path in the library | source URL
    $movies = @(
        @("Movies/Big Buck Bunny (2008)/Big Buck Bunny.mp4", "https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_640x360.m4v.zip"),
        @("Movies/Elephants Dream (2006)/Elephants Dream.mov", "https://download.blender.org/ED/elephantsdream-480-h264-st-aac.mov"),
        @("Shorts/Big Buck Bunny - 10s clip.mp4", "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4")
    )
    if ($Large) {
        $movies += , @("Movies/Big Buck Bunny 1080p (2008)/Big Buck Bunny 1080p.mp4", "https://download.blender.org/demo/movies/BBB/bbb_sunflower_1080p_30fps_normal.mp4.zip")
        $movies += , @("Movies/Sintel (2010)/Sintel.mkv", "https://download.blender.org/durian/movies/Sintel.2010.1080p.mkv")
    }
    foreach ($movie in $movies) {
        $target = Join-Path $media $movie[0]
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        $source = Get-Download $movie[1]
        if ($source.EndsWith(".m4v")) {
            # Blender's BigBuckBunny_640x360.m4v ends in a truncated MP4 box that Cove's scanner rejects;
            # a lossless remux writes a clean container.
            $staged = Join-Path $media "remux-source.m4v"
            Copy-Item $source $staged
            Invoke-Ffmpeg $media @("-v", "error", "-y", "-i", "remux-source.m4v", "-c", "copy", "-map", "0", "-movflags", "+faststart", $movie[0])
            Remove-Item $staged
        } else {
            Copy-Item $source $target
        }
        Write-Host "    $($movie[0])"
    }

    # Cut Big Buck Bunny into "episodes" (stream copy, instant) so there are enough videos to spread over workers.
    $episodes = "Shows/Big Buck Bunny Chapters/Season 1"
    New-Item -ItemType Directory -Force (Join-Path $media $episodes) | Out-Null
    for ($i = 0; $i -lt 8; $i++) {
        $name = "$episodes/S01E{0:D2}.mp4" -f ($i + 1)
        Invoke-Ffmpeg $media @("-v", "error", "-y", "-ss", ($i * 70), "-t", "70", "-i", $movies[0][0], "-c", "copy", "-map", "0", $name)
        Write-Host "    $name"
    }
}

function Register-Workers($worker1Token, $worker2Token) {
    Step "Waiting for the extension's API"
    $loaded = @(Invoke-RestMethod "$cove/api/extensions") | Where-Object { $_.id -eq $extensionId }
    if (-not $loaded) {
        throw "Cove did not load $extensionId. It must be built against a Cove.Sdk no newer than the running Cove; see ./setup.ps1 logs"
    }
    Wait-Http "$api/workers" "The remote-heavylifter extension" 120 -Json

    # Workers reach Cove on Docker's network by its service name.
    $settings = @{ coveUrlForWorkers = "http://cove:5073"; coveAuthEnabled = $false } | ConvertTo-Json
    Invoke-RestMethod -Method Put -Uri "$api/settings" -ContentType "application/json" -Body $settings | Out-Null

    $existing = Invoke-RestMethod "$api/workers"
    $workers = @($existing | ForEach-Object {
        @{ id = $_.id; name = $_.name; token = $null; url = $_.url; coveUrlOverride = $_.coveUrlOverride;
           enabled = $_.enabled; maxConcurrency = $_.maxConcurrency }
    })
    $wanted = @(@{ name = "worker-1"; token = $worker1Token; url = $null })
    if ($TwoServers) { $wanted += @{ name = "worker-2"; token = $worker2Token; url = "ws://worker-2:8750/rpc" } }

    $added = @()
    foreach ($w in $wanted) {
        if ($workers | Where-Object { $_.name -eq $w.name }) { continue }
        $workers += @{ id = $null; name = $w.name; token = $w.token; url = $w.url; coveUrlOverride = $null; enabled = $true; maxConcurrency = 2 }
        $added += $w.name
    }
    if ($added.Count -gt 0) {
        Step "Registering generation worker(s): $($added -join ', ')"
        $body = ConvertTo-Json -InputObject @($workers) -Depth 6
        Invoke-RestMethod -Method Put -Uri "$api/workers" -ContentType "application/json" -Body $body | Out-Null
    } else {
        Step "Generation workers already registered"
    }

    Step "Waiting for the workers to connect"
    $deadline = (Get-Date).AddSeconds(90)
    do {
        $health = @(Invoke-RestMethod "$api/workers/health?refresh=true")
        if (-not ($health | Where-Object { $_.state -ne "live" })) { break }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    $health | ForEach-Object {
        Write-Host ("    {0,-9} {1,-12} {2}{3}" -f $_.name, $_.state, $_.connection, $(if ($_.error) { " - $($_.error)" } else { "" }))
    }
}

function Complete-CoveSetup {
    # First-run setup is complete once an owner exists and a library path is configured (the compose
    # file seeds /media). Until an owner exists, auth-disabled requests run without any permissions.
    $status = Invoke-RestMethod "$cove/api/auth/bootstrap-status"
    if (-not $status.ownerExists) {
        Step "Creating the owner account ($ownerUser / $ownerPassword)"
        $body = @{ username = $ownerUser; password = $ownerPassword } | ConvertTo-Json
        Invoke-RestMethod -Method Post -Uri "$cove/api/auth/bootstrap-owner" -ContentType "application/json" -Body $body | Out-Null
    } else {
        Step "Owner account already exists"
    }
    $config = Invoke-RestMethod "$cove/api/system/config"
    if (-not @($config.covePaths | Where-Object { $_.path -eq "/media" })) {
        Write-Warning "Cove has no /media library path (a saved cove-config.json overrides the seed). Add it in Settings -> Library."
    }
    # Cove re-checks for an owner every 15 s before running auth-disabled requests as that owner.
    Step "Waiting for requests to run as the owner"
    Wait-Http "$cove/api/jobs" "Owner permissions" 60 -Json
}

switch ($Command) {
    "logs" { Compose logs -f --tail 100; return }
    "down" { Compose down; return }
    "reset" {
        Compose --profile multi down -v
        if (Test-Path $data) { Remove-Item -Recurse -Force $data }
        if (Test-Path $envFile) { Remove-Item -Force $envFile }
        Step "Local test data removed"
        return
    }
    "reinstall" {
        Install-Extension | Out-Null
        Step "Restarting Cove to load the new build"
        Compose restart cove
        Wait-Http "$cove/health" "Cove" 300
        Step "Done - reload the browser tab"
        return
    }
}

# ---- up --------------------------------------------------------------------------------------

& docker info --format "{{.ServerVersion}}" *> $null
if ($LASTEXITCODE) { throw "Docker is not running. Start Docker Desktop and try again." }

foreach ($dir in @("media", "cove/config/extensions", "cove/generated", "cove/backups")) {
    New-Item -ItemType Directory -Force (Join-Path $data $dir) | Out-Null
}

function New-Token {
    $bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToBase64String($bytes).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}
function Get-EnvValue($name) {
    $line = Get-Content $envFile | Where-Object { $_ -like "$name=*" } | Select-Object -First 1
    if ($line) { return ($line -split "=", 2)[1] } else { return $null }
}
if (-not (Test-Path $envFile)) { Set-Content -Path $envFile -Value @("COVE_PORT=$CovePort") -Encoding ascii }
foreach ($name in @("WORKER1_TOKEN", "WORKER2_TOKEN")) {
    if (-not (Get-EnvValue $name)) { Add-Content -Path $envFile -Value "$name=$(New-Token)" -Encoding ascii }
}
$worker1Token = Get-EnvValue "WORKER1_TOKEN"
$worker2Token = Get-EnvValue "WORKER2_TOKEN"

$zip = Install-Extension

Step "Building the generation worker image"
Compose build worker-1
New-SampleMedia

Step "Starting Cove and the generation worker(s)"
$coveWasRunning = Compose ps --status running -q cove 2>$null
Compose up -d --remove-orphans
if ($coveWasRunning) {
    Step "Restarting Cove to load the freshly installed extension"
    Compose restart cove
}
Step "Waiting for Cove (the first start runs database migrations; this can take a few minutes)"
Wait-Http "$cove/health" "Cove" 600

Complete-CoveSetup
Register-Workers $worker1Token $worker2Token

Step "Scanning the sample library"
try {
    Invoke-RestMethod -Method Post -Uri "$cove/api/metadata/scan" -ContentType "application/json" -Body "{}" | Out-Null
} catch {
    Write-Warning "Could not start the scan automatically ($($_.Exception.Message)). Run it from Settings -> Tasks."
}

Write-Host ""
Write-Host "Ready." -ForegroundColor Green
Write-Host "  Cove:                $cove   (owner: $ownerUser / $ownerPassword; auth is off for local requests)"
Write-Host "  Remote Generation:   $cove/settings/remote-generation"
Write-Host "  Extension zip:       $zip"
Write-Host "  Workers:             worker-1 dials Cove; worker-2 (-TwoServers) is dialed by Cove (tokens in local-test/.env)"
Write-Host "  Sample media:        $(Join-Path $data 'media')   (Cove: /media; workers read it from Cove over HTTP)"
Write-Host "  Generated files:     $(Join-Path $data 'cove/generated')"
Write-Host ""
Write-Host "Wait for the scan to finish (Jobs drawer), then Settings -> Remote Generation -> Generate -> Run."
