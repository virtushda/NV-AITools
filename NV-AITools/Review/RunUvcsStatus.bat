@echo off
setlocal

set "UVCS_TOOLS_EXE=C:\Program Files\UvcsTools\UvcsTools.exe"
set "WORKSPACE=%~dp0.."
if not "%~1"=="" set "WORKSPACE=%~1"
set "REVIEWS_DIR=%~dp0Reviews"

if not exist "%UVCS_TOOLS_EXE%" (
    echo ERROR: UvcsTools is not installed at "%UVCS_TOOLS_EXE%".
    goto FailedWithoutTemp
)

if not exist "%REVIEWS_DIR%" mkdir "%REVIEWS_DIR%"
if errorlevel 1 (
    echo ERROR: Could not create "%REVIEWS_DIR%".
    goto FailedWithoutTemp
)

call :SetTimestamp
set "TEMP_OUTPUT=%TEMP%\UvcsTools-status-%RANDOM%-%RANDOM%.tmp"
set "FINAL_OUTPUT=%REVIEWS_DIR%\uvcs-status-%STAMP%.json"

"%UVCS_TOOLS_EXE%" status --workspace "%WORKSPACE%" > "%TEMP_OUTPUT%"
if errorlevel 1 goto Failed
if not exist "%TEMP_OUTPUT%" goto InvalidOutput
for %%F in ("%TEMP_OUTPUT%") do if %%~zF EQU 0 goto InvalidOutput
findstr /b /c:"{" "%TEMP_OUTPUT%" >nul
if errorlevel 1 goto InvalidOutput

move /y "%TEMP_OUTPUT%" "%FINAL_OUTPUT%" >nul
if errorlevel 1 goto Failed
echo Saved UVCS status to:
echo "%FINAL_OUTPUT%"
pause
exit /b 0

:InvalidOutput
echo ERROR: UvcsTools did not produce valid status JSON.

:Failed
if exist "%TEMP_OUTPUT%" del /q "%TEMP_OUTPUT%"

:FailedWithoutTemp
echo UVCS status failed.
pause
exit /b 1

:SetTimestamp
set "STAMP=%DATE%_%TIME%"
set "STAMP=%STAMP:/=-%"
set "STAMP=%STAMP::=-%"
set "STAMP=%STAMP:.=-%"
set "STAMP=%STAMP:,=-%"
set "STAMP=%STAMP: =0%"
exit /b 0
