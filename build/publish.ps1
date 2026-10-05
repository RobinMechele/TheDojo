# Tests, then a self-contained build of the app and the CLI in artifacts/.
# artifacts/release/ holds the files a GitHub release carries (the names are what the in-app updater looks for).
param(
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifacts = Join-Path $root "artifacts"
[string[]]$versionArgs = if ($Version) { "-p:Version=$Version" } else { @() }

if (-not $SkipTests) {
    dotnet test (Join-Path $root "tests/TheDojo.Tests") -c Release @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

$appOut = Join-Path $artifacts "TheDojo"
dotnet publish (Join-Path $root "src/TheDojo") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true @versionArgs -o $appOut
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed." }

$cliOut = Join-Path $artifacts "cli"
dotnet publish (Join-Path $root "src/TheDojo.Cli") -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true @versionArgs -o $cliOut
if ($LASTEXITCODE -ne 0) { throw "Publishing the CLI failed." }

$release = Join-Path $artifacts "release"
Remove-Item $release -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $release | Out-Null
Copy-Item (Join-Path $artifacts "TheDojo\TheDojo.exe") (Join-Path $release "TheDojo-$Runtime.exe")
Copy-Item (Join-Path $artifacts "cli\dojo.exe") (Join-Path $release "dojo-$Runtime.exe")
$sums = Get-ChildItem $release -Filter *.exe | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
# LF endings and no BOM: the updater reads this file line by line.
[IO.File]::WriteAllText((Join-Path $release "SHA256SUMS.txt"), ($sums -join "`n") + "`n")

Write-Host "App: $(Join-Path $artifacts 'TheDojo\TheDojo.exe')"
Write-Host "CLI: $(Join-Path $artifacts 'cli\dojo.exe')"
Write-Host "Release files: $release"
