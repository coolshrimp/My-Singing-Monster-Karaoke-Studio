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
# Only the explicitly named distributable files enter the release package.
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$scratch = Join-Path $dist ('.build-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $scratch 'publish'
$portable = Join-Path $scratch 'portable'
$baseName = "MySingingMonsterKaraokeStudio-$version-win-x64"
try {
    New-Item -ItemType Directory -Path $portable -Force | Out-Null
    & dotnet publish $project -c Release -r win-x64 --self-contained true --force --artifacts-path (Join-Path $scratch 'build') `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=false `
        -p:DebugType=embedded -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed ($LASTEXITCODE)." }
    $executable = Join-Path $publish 'MySingingMonsterKaraokeStudio.exe'
    if (!(Test-Path -LiteralPath $executable)) { throw 'Published EXE is missing.' }
    $builtVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion.Split('+')[0]
    if ($builtVersion -cne $version) { throw "EXE version '$builtVersion' does not match '$version'." }

    Copy-Item -LiteralPath $executable -Destination (Join-Path $portable 'MySingingMonsterKaraokeStudio.exe')
    foreach ($file in @('README.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $portable
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install-ffmpeg.cmd') -Destination $portable
    $portableTools = Join-Path $portable 'Tools'
    New-Item -ItemType Directory -Path $portableTools -Force | Out-Null
    foreach ($file in @('FFmpeg-LICENSE.txt', 'FFmpeg-SOURCE.txt')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "Tools\$file") -Destination $portableTools
    }
    # Keep README image links working in the portable download as well as GitHub.
    $screenshots = Join-Path $repoRoot 'snapshots'
    if (Test-Path -LiteralPath $screenshots) {
        $portableScreenshots = Join-Path $portable 'snapshots'
        New-Item -ItemType Directory -Path $portableScreenshots -Force | Out-Null
        Get-ChildItem -LiteralPath $screenshots -Filter '*.png' -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $portableScreenshots
        }
    }
    $readmeAssets = Join-Path $portable 'Assets'
    New-Item -ItemType Directory -Path $readmeAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'Assets\StudioIcon.png') -Destination $readmeAssets
    @"
My Singing Monster Karaoke Studio $version
Windows x64 portable release

Extract this ZIP into a normal folder and run MySingingMonsterKaraokeStudio.exe.
.NET is included. Microsoft Edge WebView2 Runtime is required.
FFmpeg is not bundled. Use Install FFmpeg in the app or install-ffmpeg.cmd.
FFmpeg setup downloads and verifies the converter; internet is needed on first setup.
See README.md for the editing, library, game import and recovery guide.
"@ | Set-Content -LiteralPath (Join-Path $portable 'START-HERE.txt') -Encoding utf8

    $exeAsset = Join-Path $dist "$baseName.exe"
    $zipAsset = Join-Path $dist "$baseName.zip"
    Copy-Item -LiteralPath $executable -Destination $exeAsset -Force
    # Replace only this version's ZIP, leaving other release versions intact.
    if (Test-Path -LiteralPath $zipAsset) { Remove-Item -LiteralPath $zipAsset }
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zipAsset -CompressionLevel Optimal
    $hashes = foreach ($asset in @($exeAsset, $zipAsset)) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $asset)
    }
    $hashes | Set-Content -LiteralPath (Join-Path $dist "$baseName-SHA256SUMS.txt") -Encoding ascii
    Write-Host "Release $version ready in $dist"
    Write-Host "EXE, portable ZIP and SHA256 checksums created. FFmpeg is downloaded separately."
} finally {
    # This exact GUID directory was created above within the resolved dist folder.
    $resolvedDist = [IO.Path]::GetFullPath($dist).TrimEnd('\') + '\'
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    if (!$resolvedScratch.StartsWith($resolvedDist, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside dist.'
    }
    if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
