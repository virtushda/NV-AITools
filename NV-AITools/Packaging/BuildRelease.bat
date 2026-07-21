@echo off
setlocal EnableExtensions

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "PROJECT=%ROOT%\NV-AITools.csproj"
set "VERSION=%~1"
set "MODE=%~2"
if "%VERSION%"=="" set "VERSION=1.0.0"

set "ARTIFACTS=%ROOT%\Artifacts"
set "PUBLISH_DIR=%ARTIFACTS%\publish\win-x64"
set "INSTALLER_DIR=%ARTIFACTS%\installer"
set "KIT_STAGE=%ARTIFACTS%\workspace-kit"
set "KIT_ZIP=%ARTIFACTS%\UvcsTools-WorkspaceKit-%VERSION%.zip"

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%INSTALLER_DIR%" rmdir /s /q "%INSTALLER_DIR%"
if exist "%KIT_STAGE%" rmdir /s /q "%KIT_STAGE%"
if exist "%ARTIFACTS%\UvcsTools-WorkspaceKit-*.zip" del /q "%ARTIFACTS%\UvcsTools-WorkspaceKit-*.zip"

mkdir "%PUBLISH_DIR%" || goto Failed
mkdir "%INSTALLER_DIR%" || goto Failed
mkdir "%KIT_STAGE%\Review" || goto Failed

echo Restoring UvcsTools %VERSION%...
dotnet restore "%PROJECT%" -r win-x64
if errorlevel 1 goto Failed

echo Publishing self-contained single-file executable...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true --no-restore --output "%PUBLISH_DIR%" -p:Version="%VERSION%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:PublishAot=false -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true
if errorlevel 1 goto Failed
if not exist "%PUBLISH_DIR%\UvcsTools.exe" (
    echo ERROR: Publish completed without UvcsTools.exe.
    goto Failed
)

copy /y "%ROOT%\Review\RunUvcsStatus.bat" "%KIT_STAGE%\Review\" >nul || goto Failed
copy /y "%ROOT%\Review\RunPendingChangesDiffs.bat" "%KIT_STAGE%\Review\" >nul || goto Failed
copy /y "%ROOT%\Review\RunChangesetDiffs.bat" "%KIT_STAGE%\Review\" >nul || goto Failed

where tar.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Windows tar.exe is required to create the workspace-kit ZIP.
    goto Failed
)

echo Creating workspace kit...
tar.exe -a -c -f "%KIT_ZIP%" -C "%KIT_STAGE%" Review
if errorlevel 1 goto Failed

if /i "%MODE%"=="--publish-only" goto PublishComplete

call :FindInnoCompiler
if not defined ISCC_EXE (
    echo ERROR: ISCC.exe was not found.
    echo Install Inno Setup from https://jrsoftware.org/isdl.php and run this script again.
    echo To publish without an installer, use: BuildRelease.bat %VERSION% --publish-only
    exit /b 3
)

echo Compiling installer with "%ISCC_EXE%"...
"%ISCC_EXE%" /DAppVersion=%VERSION% /O"%INSTALLER_DIR%" "%ROOT%\Packaging\UvcsTools.iss"
if errorlevel 1 goto Failed

echo.
echo Release artifacts created:
echo   "%INSTALLER_DIR%\UvcsTools-Setup-%VERSION%.exe"
echo   "%KIT_ZIP%"
exit /b 0

:PublishComplete
echo.
echo Publish artifacts created:
echo   "%PUBLISH_DIR%\UvcsTools.exe"
echo   "%KIT_ZIP%"
exit /b 0

:FindInnoCompiler
set "ISCC_EXE="
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if not defined ISCC_EXE set "ISCC_EXE=%%I"
if defined ISCC_EXE exit /b 0

for %%I in (
    "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
    "%ProgramFiles%\Inno Setup 7\ISCC.exe"
    "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
    "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
    "%ProgramFiles%\Inno Setup 6\ISCC.exe"
    "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
) do if not defined ISCC_EXE if exist "%%~I" set "ISCC_EXE=%%~I"
exit /b 0

:Failed
echo ERROR: Release build failed.
exit /b 1
