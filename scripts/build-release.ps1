[CmdletBinding()]
param([string]$Tag)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'MySingingMonsterKaraokeStudio.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid project version: $version"
}
if ($Tag -and $Tag -cne 'live' -and $Tag -cne "v$version") {
    throw "Tag '$Tag' must be 'live' or match project version 'v$version'. Update the project and CHANGELOG before tagging."
}

# Every run publishes freshly compiled source into its own temporary directory.
# Only the standalone EXE is distributed.
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$scratch = Join-Path $dist ('.build-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $scratch 'publish'
try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true --force --artifacts-path (Join-Path $scratch 'build') `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=false `
        -p:DebugType=embedded -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed ($LASTEXITCODE)." }
    $executable = Join-Path $publish 'MySingingMonsterKaraokeStudio.exe'
    if (!(Test-Path -LiteralPath $executable)) { throw 'Published EXE is missing.' }
    $builtVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion.Split('+')[0]
    if ($builtVersion -cne $version) { throw "EXE version '$builtVersion' does not match '$version'." }

    $exeAsset = Join-Path $dist 'MySingingMonsterKaraokeStudio.exe'
    Copy-Item -LiteralPath $executable -Destination $exeAsset -Force
    Write-Host "Release $version ready in $dist"
    Write-Host 'MySingingMonsterKaraokeStudio.exe created. FFmpeg is installed through the app.'
} finally {
    # This exact GUID directory was created above within the resolved dist folder.
    $resolvedDist = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if (!$resolvedScratch.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside dist.'
    }
    if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
