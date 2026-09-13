# Builds both release artifacts into dist\:
#   NSQS-<version>-setup.exe     installer (per-user, adds Start Menu + uninstaller)
#   NSQS-<version>-portable.exe  single exe, run from anywhere
# Both are framework-dependent and need the .NET 8 Desktop Runtime.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$projectDir = "$root\Nsqs"
$iconPath = "$projectDir\app.ico"

[xml]$proj = Get-Content "$projectDir\Nsqs.csproj"
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "No <Version> found in Nsqs.csproj" }
Write-Host "Building Network Share Quick Search (NSQS) $version" -ForegroundColor Cyan

if (-not (Test-Path $iconPath)) {
    Write-Host "Generating app.ico" -ForegroundColor Cyan
    dotnet run --project "$root\tools\IconGen" -c Release -- $iconPath
    if ($LASTEXITCODE -ne 0) { throw "icon generation failed" }
}

Remove-Item "$root\publish", "$root\dist" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$root\dist" -Force | Out-Null

dotnet publish "$projectDir" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item "$root\publish\NSQS.exe" "$root\dist\NSQS-$version-portable.exe"

if (-not (Test-Path $iscc)) { throw "Inno Setup not found at $iscc" }
& $iscc "/DAppVersion=$version" "$root\installer\NSQS.iss" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }

Write-Host "`nDone:" -ForegroundColor Green
Get-ChildItem "$root\dist" | ForEach-Object {
    "{0,-34} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)
}
