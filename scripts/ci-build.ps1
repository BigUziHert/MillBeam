#Requires -Version 7.0
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
$project = Join-Path $root 'GenmitsuControl.Desktop/GenmitsuControl.Desktop.csproj'
$baseVersion = [string]([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($baseVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The desktop project needs a three-part base version.' }
if ($env:GITHUB_REF -like 'refs/tags/*') {
    if ($env:GITHUB_REF -cne "refs/tags/v$baseVersion") { throw 'A stable release tag must exactly match the desktop project version.' }
    $version = $baseVersion
} else {
    if ($env:GITHUB_RUN_NUMBER -notmatch '^[1-9]\d*$' -or $env:GITHUB_RUN_ATTEMPT -notmatch '^[1-9]\d*$') {
        throw 'CI requires a GitHub run number and attempt to create a unique dev version.'
    }
    $version = "$baseVersion-dev.$($env:GITHUB_RUN_NUMBER).$($env:GITHUB_RUN_ATTEMPT)"
}
if ($env:GITHUB_SHA -notmatch '^[0-9a-f]{40}$') { throw 'CI requires the complete source commit SHA.' }
if ($env:GITHUB_OUTPUT) { "version=$version" | Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 }
Write-Host "Building MillBeam $version from $($env:GITHUB_SHA)"

$release = Join-Path $root 'release'
$results = Join-Path $release 'test-results'
$publish = Join-Path $release 'win-x64'
$portable = Join-Path $release 'portable'
$dist = Join-Path $release 'distributables'
foreach ($directory in @($results, $publish, $portable, $dist)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

foreach ($testProject in @('GenmitsuControl.Tests', 'GenmitsuControl.UpdateTests')) {
    & $Dotnet run --project "$testProject/$testProject.csproj" -c Release 2>&1 |
        Tee-Object -FilePath (Join-Path $results "$testProject.txt")
    if ($LASTEXITCODE -ne 0) { throw "$testProject failed with exit code $LASTEXITCODE." }
}

& $Dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    "-p:InformationalVersion=$version" -p:IncludeSourceRevisionInInformationalVersion=false `
    -o $publish 2>&1 | Tee-Object -FilePath (Join-Path $results 'publish.txt')
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }
$exe = Join-Path $publish 'MillBeam.exe'
$builtVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($builtVersion -cne $version) { throw "Executable version '$builtVersion' does not match '$version'." }

# Both modes explicitly create simulated/test transports; they never open a
# machine port. Exercise the exact executable that will be uploaded.
& (Join-Path $PSScriptRoot 'ci-smoke.ps1') -Executable $exe -ResultsDirectory $results

Copy-Item -LiteralPath $exe -Destination $portable
foreach ($name in @('README.md', 'VALIDATION.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $portable
}
$dotnetRoot = Split-Path (Get-Command $Dotnet -ErrorAction Stop).Source -Parent
foreach ($notice in @(@('LICENSE.txt', 'DOTNET-LICENSE.txt'), @('ThirdPartyNotices.txt', 'DOTNET-NOTICES.txt'))) {
    Copy-Item -LiteralPath (Join-Path $dotnetRoot $notice[0]) -Destination (Join-Path $portable $notice[1])
}
Copy-Item -LiteralPath (Join-Path $results 'ui/ui-smoke-results.txt') -Destination (Join-Path $portable 'UI-TEST-RESULTS.txt')
Copy-Item -LiteralPath (Join-Path $results 'shutdown/shutdown-smoke-results.txt') -Destination (Join-Path $portable 'SHUTDOWN-TEST-RESULTS.txt')
Copy-Item -LiteralPath (Join-Path $results 'update-ui/update-ui-smoke-results.txt') -Destination (Join-Path $portable 'UPDATE-UI-TEST-RESULTS.txt')
Copy-Item -LiteralPath (Join-Path $results 'GenmitsuControl.UpdateTests.txt') -Destination (Join-Path $portable 'UPDATE-TEST-RESULTS.txt')
Copy-Item -LiteralPath $exe -Destination $dist
$zip = Join-Path $dist 'MillBeam-Windows-x64.zip'
if (Test-Path -LiteralPath $zip) { throw 'The distributable ZIP already exists; use a fresh CI checkout.' }
[IO.Compression.ZipFile]::CreateFromDirectory($portable, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$assetNames = @('MillBeam.exe', 'MillBeam-Windows-x64.zip')
$hashes = foreach ($name in $assetNames) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'), $hashes, [Text.UTF8Encoding]::new($false))
$metadata = [ordered]@{ version = $version; commit = $env:GITHUB_SHA; sourceRef = $env:GITHUB_REF }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dist 'release-manifest.json') -Encoding utf8
Write-Host "Tested distributables ready: $dist"
