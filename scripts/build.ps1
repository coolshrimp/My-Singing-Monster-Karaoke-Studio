$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'MySingingMonsterKaraokeStudio.csproj'
$outDir = Join-Path $repoRoot 'publish-win-x64'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'The .NET SDK is required only to BUILD this project.' -ForegroundColor Yellow
    Write-Host 'Install .NET 8 SDK, then run this script again.' -ForegroundColor Yellow
    exit 1
}

Write-Host 'Publishing self-contained Windows x64 single EXE...'
# Refresh WPF markup as well as code when source was copied between project directories.
dotnet build $project -c Release -t:Rebuild
if ($LASTEXITCODE -ne 0) { throw 'Build failed. See the errors above.' }
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -o $outDir

if ($LASTEXITCODE -ne 0) { throw 'Publish failed. See the build errors above.' }

$exe = Join-Path $outDir 'MySingingMonsterKaraokeStudio.exe'
if (-not (Test-Path $exe)) { throw 'Publish finished but MySingingMonsterKaraokeStudio.exe was not found.' }

Write-Host ''
Write-Host 'READY:' -ForegroundColor Green
Write-Host $exe -ForegroundColor Green
