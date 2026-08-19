@echo off
setlocal EnableExtensions

set "APPLICATION_TARGET=%LOCALAPPDATA%\Programs\NV-AITools"
set "SKILL_TARGET=%USERPROFILE%\.agents\skills\use-nv-ai-tools"
set "LEGACY_SKILL_TARGET=%USERPROFILE%\.agents\skills\use-uvcs-tools"

if exist "%APPLICATION_TARGET%\NV-AITools.exe" (
    "%APPLICATION_TARGET%\NV-AITools.exe" broker-stop
    if errorlevel 1 (
        echo ERROR: The NV-AITools broker could not be stopped. No application files were removed.
        exit /b 1
    )
)

reg.exe delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "NV-AITools" /f >nul 2>nul

if exist "%APPLICATION_TARGET%" rmdir /s /q "%APPLICATION_TARGET%"
if exist "%APPLICATION_TARGET%" (
    echo ERROR: Could not remove "%APPLICATION_TARGET%".
    exit /b 1
)

if exist "%SKILL_TARGET%" rmdir /s /q "%SKILL_TARGET%"
if exist "%SKILL_TARGET%" (
    echo ERROR: Could not remove "%SKILL_TARGET%".
    exit /b 1
)

if exist "%LEGACY_SKILL_TARGET%" rmdir /s /q "%LEGACY_SKILL_TARGET%"
if exist "%LEGACY_SKILL_TARGET%" (
    echo ERROR: Could not remove "%LEGACY_SKILL_TARGET%".
    exit /b 1
)

echo Removed NV-AITools, its configuration and logs, automatic startup entry, and Codex skill.
echo Workspace Review folders, .nv-ai-tools queues, and generated reports were not removed.
exit /b 0
