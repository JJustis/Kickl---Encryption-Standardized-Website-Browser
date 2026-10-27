@echo off
rem Starts Kickl Browser against a THROWAWAY test shop and test CA, so the whole claim flow can be tried with no Bitcoin
rem and without touching your real data, the real shop or the real CA.
rem
rem   test shop   http://localhost/btcshop-test/         (red TEST SHOP banner, "mark as paid" button)
rem   test CA     http://localhost/btcshop-test/secca/    (the CA website, with its own throwaway root)
rem   test data   %USERPROFILE%\kickl-test\data           (separate profiles, pins and registry)
rem
rem Try it:
rem   1. Sign in with 1234 (demo profile).
rem   2. Open any page and press CLAIM SITE, then OPEN BTC SHOP (it opens the TEST shop).
rem   3. In the shop press "claim this site", then the red TEST button, then "download ownership certificate".
rem   4. Back in the browser press IMPORT CERTIFICATE and choose the .kcert file.
rem      The browser sends it to the test CA by itself; the badge turns to VERIFIED . TEST CA.
rem   5. Open http://localhost/btcshop-test/secca/ to see it in the CA's public log, or drop a .kcert there yourself.
rem
rem When you are done: delete C:\xampp\htdocs\btcshop-test, C:\xampp\btcshop-test-private and %USERPROFILE%\kickl-test.
setlocal
cd /d "%~dp0"

set "T=%USERPROFILE%\kickl-test"
if not exist "%T%\ca\root-public.json" (
  echo The test CA has not been set up: %T%\ca\root-public.json is missing.
  pause
  exit /b 1
)

set "KICKL_SHOP_URL=http://localhost/btcshop-test/"
set "KICKL_CA_URL=http://localhost/btcshop-test/secca/"
set "KICKL_TEST_CA_ROOT=%T%\ca\root-public.json"

echo Building Kickl Browser (Release)...
dotnet build src\Kickl.Browser\Kickl.Browser.csproj -c Release --nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed. Nothing was started.
  pause
  exit /b 1
)

echo Starting the TEST setup. Shop: %KICKL_SHOP_URL%  CA: %KICKL_CA_URL%
start "" "src\Kickl.Browser\bin\Release\net10.0\Kickl.Browser.exe" --demo --data "%T%\data"
