@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: The .NET SDK was not found.
  echo Install the .NET 10 SDK, then run build.bat again.
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  echo.
  echo The publish folder is created only after a successful build.
  pause
  exit /b 1
)

echo Publishing CaptionClock as a self-contained Windows x64 app...
dotnet publish CaptionClock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "%~dp0publish" > "%~dp0build.log" 2>&1
set BUILD_EXIT=%ERRORLEVEL%
type "%~dp0build.log"
if not "%BUILD_EXIT%"=="0" (
  echo.
  echo ERROR: Build failed with exit code %BUILD_EXIT%.
  echo The full output is saved in build.log.
  echo Check that the .NET 10 SDK is installed and that internet access is available for the first publish.
  echo The publish folder appears only after a successful build.
  echo.
  pause
  exit /b 1
)

echo.
echo Build complete: %~dp0publish\CaptionClock.exe
echo.
pause
endlocal
