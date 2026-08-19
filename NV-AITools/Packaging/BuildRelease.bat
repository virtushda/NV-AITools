@echo off
setlocal EnableExtensions DisableDelayedExpansion

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
for %%I in ("%ROOT%\..") do set "REPOSITORY_ROOT=%%~fI"
set "PROJECT=%ROOT%\NV-AITools.csproj"
set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=1.2.0"

set "ARTIFACTS=%ROOT%\Artifacts"
set "PUBLISH_DIR=%ARTIFACTS%\publish\win-x64"
set "PACKAGE_NAME=NV-AITools-%VERSION%-win-x64"
set "PACKAGE_STAGE=%ARTIFACTS%\package\%PACKAGE_NAME%"
set "PACKAGE_ZIP=%ARTIFACTS%\%PACKAGE_NAME%.zip"

set "NVAI_RELEASE_VERSION=%VERSION%"
set "NVAI_ARTIFACTS=%ARTIFACTS%"
set "NVAI_PACKAGE_STAGE=%PACKAGE_STAGE%"
set "NVAI_PACKAGE_ZIP=%PACKAGE_ZIP%"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$version = $env:NVAI_RELEASE_VERSION; if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { exit 1 }; $root = [IO.Path]::GetFullPath($env:NVAI_ARTIFACTS).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar; $stage = [IO.Path]::GetFullPath($env:NVAI_PACKAGE_STAGE); $zip = [IO.Path]::GetFullPath($env:NVAI_PACKAGE_ZIP); if (-not $stage.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not $zip.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { exit 1 }"
if errorlevel 1 (
    echo ERROR: Version must use major.minor.patch with non-negative integers, and release paths must remain under Artifacts.
    echo No release files were changed.
    exit /b 2
)

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%PACKAGE_STAGE%" rmdir /s /q "%PACKAGE_STAGE%"
if exist "%PACKAGE_ZIP%" del /q "%PACKAGE_ZIP%"
if exist "%ARTIFACTS%\installer" rmdir /s /q "%ARTIFACTS%\installer"
if exist "%ARTIFACTS%\workspace-kit" rmdir /s /q "%ARTIFACTS%\workspace-kit"
if exist "%ARTIFACTS%\TestProfile" rmdir /s /q "%ARTIFACTS%\TestProfile"
for %%I in ("%ARTIFACTS%\UvcsTools-WorkspaceKit-*.zip") do if exist "%%~fI" del /q "%%~fI"

mkdir "%PUBLISH_DIR%" || goto Failed
mkdir "%PACKAGE_STAGE%\SkillTemplate\use-nv-ai-tools\agents" || goto Failed
mkdir "%PACKAGE_STAGE%\WorkspaceKit\Review" || goto Failed

echo Restoring NV-AITools %VERSION%...
dotnet restore "%PROJECT%" -r win-x64
if errorlevel 1 goto Failed

echo Publishing self-contained single-file executable...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true --no-restore --output "%PUBLISH_DIR%" -p:Version="%VERSION%" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:PublishAot=false -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true
if errorlevel 1 goto Failed
if not exist "%PUBLISH_DIR%\NV-AITools.exe" (
    echo ERROR: Publish completed without NV-AITools.exe.
    goto Failed
)

copy /y "%PUBLISH_DIR%\NV-AITools.exe" "%PACKAGE_STAGE%\" >nul || goto Failed
copy /y "%ROOT%\config.ini" "%PACKAGE_STAGE%\" >nul || goto Failed
copy /y "%ROOT%\Packaging\Install.bat" "%PACKAGE_STAGE%\" >nul || goto Failed
copy /y "%ROOT%\Packaging\Uninstall.bat" "%PACKAGE_STAGE%\" >nul || goto Failed
copy /y "%ROOT%\Packaging\StartBroker.vbs" "%PACKAGE_STAGE%\" >nul || goto Failed
copy /y "%ROOT%\.codex\skills\use-nv-ai-tools\SKILL.md" "%PACKAGE_STAGE%\SkillTemplate\use-nv-ai-tools\" >nul || goto Failed
copy /y "%ROOT%\.codex\skills\use-nv-ai-tools\agents\openai.yaml" "%PACKAGE_STAGE%\SkillTemplate\use-nv-ai-tools\agents\" >nul || goto Failed
copy /y "%ROOT%\Review\RunUvcsStatus.bat" "%PACKAGE_STAGE%\WorkspaceKit\Review\" >nul || goto Failed
copy /y "%ROOT%\Review\RunPendingChangesDiffs.bat" "%PACKAGE_STAGE%\WorkspaceKit\Review\" >nul || goto Failed
copy /y "%ROOT%\Review\RunChangesetDiffs.bat" "%PACKAGE_STAGE%\WorkspaceKit\Review\" >nul || goto Failed
copy /y "%REPOSITORY_ROOT%\LICENSE" "%PACKAGE_STAGE%\LICENSE.txt" >nul || goto Failed

call :WriteReadme "%PACKAGE_STAGE%\README.txt"
if errorlevel 1 goto Failed

where tar.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Windows tar.exe is required to create the release ZIP.
    goto Failed
)

echo Creating portable release ZIP...
tar.exe -a -c -f "%PACKAGE_ZIP%" -C "%ARTIFACTS%\package" "%PACKAGE_NAME%"
if errorlevel 1 goto Failed

echo.
echo Portable release created:
echo   "%PACKAGE_ZIP%"
exit /b 0

:WriteReadme
> "%~1" echo(NV-AITools %VERSION%
>> "%~1" echo(
>> "%~1" echo(REQUIREMENTS
>> "%~1" echo(1. Windows x64.
>> "%~1" echo(2. Unity Version Control or Plastic SCM with cm.exe available on PATH.
>> "%~1" echo(3. Git with git.exe available on PATH.
>> "%~1" echo(
>> "%~1" echo(NV-AITools is self-contained. The receiving computer does not need the .NET runtime.
>> "%~1" echo(
>> "%~1" echo(VERSION 1.2
>> "%~1" echo(Pending and changeset diff commands use rolling, ordered pipelines with up to 16 Plastic operations and 16 local comparisons per active command.
>> "%~1" echo(Rename detection is disabled because per-file comparisons cannot identify cross-file renames correctly.
>> "%~1" echo(Pending-change diffs accept repeatable --file-filter filename globs.
>> "%~1" echo(Filters are case-insensitive, match basenames in any folder, support * and ?, and combine with OR.
>> "%~1" echo(
>> "%~1" echo(INSTALLATION
>> "%~1" echo(1. Extract the entire ZIP.
>> "%~1" echo(2. Run Install.bat. Administrator access is not required.
>> "%~1" echo(3. Edit config.ini beside the installed application and add each UVCS workspace root.
>> "%~1" echo(4. Use Reload Configuration from the NV-AITools tray menu.
>> "%~1" echo(5. Restart your AI application so it discovers the installed skill.
>> "%~1" echo(
>> "%~1" echo(The application is installed to:
>> "%~1" echo(  %%LOCALAPPDATA%%\Programs\NV-AITools
>> "%~1" echo(
>> "%~1" echo(The Codex skill is installed to:
>> "%~1" echo(  %%USERPROFILE%%\.agents\skills\use-nv-ai-tools
>> "%~1" echo(
>> "%~1" echo(BROKER
>> "%~1" echo(NV-AITools runs in the system tray and starts automatically through the included hidden startup script when you sign in.
>> "%~1" echo(The tray menu opens config.ini and broker.log, reloads configuration, or exits.
>> "%~1" echo(Each configured workspace receives a .nv-ai-tools coordination directory.
>> "%~1" echo(Add .nv-ai-tools/ to that workspace's source-control ignore rules.
>> "%~1" echo(AI clients submit fixed read-only operations through this directory; only the broker invokes cm.exe and git.exe.
>> "%~1" echo(
>> "%~1" echo(SECURITY MODEL
>> "%~1" echo(NV-AITools is intentionally installed in a directory writable by the current user.
>> "%~1" echo(Run AI agents inside a sandbox that prevents writes outside approved workspaces.
>> "%~1" echo(Without that boundary, an unrestricted AI can replace NV-AITools.exe and execute arbitrary code.
>> "%~1" echo(Using NV-AITools with an unsandboxed AI means accepting that risk.
>> "%~1" echo(
>> "%~1" echo(WORKSPACE SETUP
>> "%~1" echo(Add each UVCS workspace root to the installed config.ini.
>> "%~1" echo(The optional WorkspaceKit\Review wrappers save human-run reports under Review\Reviews.
>> "%~1" echo(
>> "%~1" echo(UPDATES AND REMOVAL
>> "%~1" echo(Run Install.bat again to update an existing installation.
>> "%~1" echo(Run Uninstall.bat to remove the application and Codex skill.
>> "%~1" echo(Uninstall.bat does not remove workspace Review folders, .nv-ai-tools queues, or generated reports.
exit /b 0

:Failed
echo ERROR: Release build failed.
exit /b 1
