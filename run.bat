@echo off
rem Starts Kickl Browser from source.
rem Optional first argument: the data directory that holds profiles/ and shared/.
rem The default is the .kickl-mail-ui folder where this machine's profiles live.
setlocal
cd /d "%~dp0"

set "DATA=%~1"
if "%DATA%"=="" set "DATA=%~dp0.kickl-mail-ui"

echo Building Kickl Browser (Release)...
dotnet build src\Kickl.Browser\Kickl.Browser.csproj -c Release --nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed. Nothing was started.
  pause
  exit /b 1
)

echo Starting with data directory: %DATA%
start "" "src\Kickl.Browser\bin\Release\net10.0\Kickl.Browser.exe" --data "%DATA%"
