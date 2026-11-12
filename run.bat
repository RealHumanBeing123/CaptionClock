@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0publish\CaptionClock.exe" goto run_published

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET was not found, and there is no published app in the publish folder.
  echo Install the .NET 10 SDK and run build.bat, or copy in a completed publish folder.
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  echo.
  pause
  exit /b 1
)

echo Starting CaptionClock from source. Startup details will be shown when the app exits.
dotnet run --project "%~dp0CaptionClock.csproj" > "%~dp0run.log" 2>&1
set RUN_EXIT=%ERRORLEVEL%
type "%~dp0run.log"
if not "%RUN_EXIT%"=="0" (
  echo.
  echo ERROR: CaptionClock could not start. Exit code: %RUN_EXIT%.
  echo The output is saved in run.log.
  echo Verify that the .NET 10 SDK is installed.
)
echo.
pause
endlocal
exit /b %RUN_EXIT%

:run_published
echo Launching the published CaptionClock app...
"%~dp0publish\CaptionClock.exe"
set RUN_EXIT=%ERRORLEVEL%
if not "%RUN_EXIT%"=="0" echo The app exited with code %RUN_EXIT%.
echo.
pause
endlocal
exit /b %RUN_EXIT%
