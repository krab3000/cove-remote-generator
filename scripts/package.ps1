# Builds the Cove extension ZIP (install it from Cove: Settings -> Extensions -> Install from ZIP).
#   pwsh scripts/package.ps1 [-Configuration Release] [-SkipFrontendInstall]
param(
    [string]$Configuration = "Release",
    [switch]$SkipFrontendInstall
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $repo "VERSION") -Raw).Trim()
$frontend = Join-Path $repo "extension/frontend"
$project = Join-Path $repo "extension/backend/RemoteHeavylifter/RemoteHeavylifter.csproj"
$out = Join-Path $repo "artifacts"
$publish = Join-Path $out "publish"
$zip = Join-Path $out "remote-heavylifter-$version.zip"

Push-Location $frontend
try {
    if (-not $SkipFrontendInstall) { npm ci --no-audit --no-fund; if ($LASTEXITCODE) { throw "npm ci failed" } }
    npm run build; if ($LASTEXITCODE) { throw "frontend build failed" }
}
finally { Pop-Location }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish $project -c $Configuration -o $publish; if ($LASTEXITCODE) { throw "dotnet publish failed" }

foreach ($required in @("extension.json", "RemoteHeavylifter.dll", "assets/ui.mjs")) {
    if (-not (Test-Path (Join-Path $publish $required))) { throw "Package is missing $required" }
}
$hostAssemblies = Get-ChildItem $publish -Filter *.dll | Where-Object { $_.Name -match '^(Cove\.|Microsoft\.EntityFrameworkCore|Npgsql|Pgvector)' }
if ($hostAssemblies) { throw "Package contains host assemblies: $($hostAssemblies.Name -join ', ')" }

if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip
Write-Host "Built $zip"
Get-ChildItem $publish -Recurse -File | ForEach-Object { "  " + $_.FullName.Substring($publish.Length + 1) }
