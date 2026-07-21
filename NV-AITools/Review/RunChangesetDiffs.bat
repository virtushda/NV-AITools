@echo off
setlocal

set "UVCS_TOOLS_EXE=C:\Program Files\UvcsTools\UvcsTools.exe"
set "WORKSPACE=%~dp0.."
set "REVIEWS_DIR=%~dp0Reviews"
set "FROM_CHANGESET=%~1"
set "TO_CHANGESET=%~2"
set "ALGORITHM=%~3"

if "%FROM_CHANGESET%"=="" set /p "FROM_CHANGESET=Starting changeset: "
if "%TO_CHANGESET%"=="" set /p "TO_CHANGESET=Ending changeset: "

if not exist "%UVCS_TOOLS_EXE%" (
    echo ERROR: UvcsTools is not installed at "%UVCS_TOOLS_EXE%".
    goto FailedWithoutTemp
)

if not exist "%REVIEWS_DIR%" mkdir "%REVIEWS_DIR%"
if errorlevel 1 (
    echo ERROR: Could not create "%REVIEWS_DIR%".
    goto FailedWithoutTemp
)

set "TEMP_OUTPUT=%TEMP%\UvcsTools-changesets-%RANDOM%-%RANDOM%.tmp"
set "FINAL_OUTPUT=%REVIEWS_DIR%\plastic-log-diff-%FROM_CHANGESET%-to-%TO_CHANGESET%.patch"

if "%ALGORITHM%"=="" (
    "%UVCS_TOOLS_EXE%" changeset-diffs --from "%FROM_CHANGESET%" --to "%TO_CHANGESET%" --workspace "%WORKSPACE%" > "%TEMP_OUTPUT%"
) else (
    "%UVCS_TOOLS_EXE%" changeset-diffs --from "%FROM_CHANGESET%" --to "%TO_CHANGESET%" --workspace "%WORKSPACE%" --algorithm "%ALGORITHM%" > "%TEMP_OUTPUT%"
)
if errorlevel 1 goto Failed
if not exist "%TEMP_OUTPUT%" goto InvalidOutput

move /y "%TEMP_OUTPUT%" "%FINAL_OUTPUT%" >nul
if errorlevel 1 goto Failed
echo Saved changeset differences to:
echo "%FINAL_OUTPUT%"
pause
exit /b 0

:InvalidOutput
echo ERROR: UvcsTools did not create a patch result.

:Failed
if exist "%TEMP_OUTPUT%" del /q "%TEMP_OUTPUT%"

:FailedWithoutTemp
echo Changeset comparison failed.
pause
exit /b 1
