@echo off
setlocal
set "STUDIO_APP=%~dp0MySingingMonsterKaraokeStudio.exe"
if not exist "%STUDIO_APP%" set "STUDIO_APP=%~dp0..\publish-win-x64\MySingingMonsterKaraokeStudio.exe"
if not exist "%STUDIO_APP%" (
    echo The studio app was not found. Build with scripts\build.cmd or extract the portable release ZIP.
    pause
    exit /b 1
)
start "" "%STUDIO_APP%" --install-ffmpeg
