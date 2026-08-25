@echo off
setlocal EnableExtensions

where cm.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Plastic SCM or Unity Version Control is not installed, or cm.exe is not available on PATH.
    echo Install the Unity Version Control client and verify that "where cm.exe" succeeds, then run Install.bat again.
    exit /b 3
)

where git.exe >nul 2>nul
if errorlevel 1 (
    echo ERROR: Git is not installed, or git.exe is not available on PATH.
    echo Install Git and verify that "where git.exe" succeeds, then run Install.bat again.
    exit /b 3
)

set "APPLICATION_SOURCE=%~dp0NV-AITools.exe"
set "CONFIG_SOURCE=%~dp0config.ini"
set "STARTUP_SOURCE=%~dp0StartBroker.vbs"
set "SKILL_SOURCE=%~dp0SkillTemplate\use-nv-ai-tools"
set "APPLICATION_TARGET=%LOCALAPPDATA%\Programs\NV-AITools"
set "STARTUP_TARGET=%APPLICATION_TARGET%\StartBroker.vbs"
set "SKILL_TARGET=%USERPROFILE%\.agents\skills\use-nv-ai-tools"
set "LEGACY_SKILL_TARGET=%USERPROFILE%\.agents\skills\use-uvcs-tools"

if not exist "%APPLICATION_SOURCE%" (
    echo ERROR: The package is missing "%APPLICATION_SOURCE%".
    exit /b 1
)
if not exist "%CONFIG_SOURCE%" (
    echo ERROR: The package is missing "%CONFIG_SOURCE%".
    exit /b 1
)
if not exist "%STARTUP_SOURCE%" (
    echo ERROR: The package is missing "%STARTUP_SOURCE%".
    exit /b 1
)
if not exist "%SKILL_SOURCE%\SKILL.md" (
    echo ERROR: The package is missing "%SKILL_SOURCE%\SKILL.md".
    exit /b 1
)

"%APPLICATION_SOURCE%" broker-check-dependencies
if errorlevel 1 (
    echo ERROR: The installed Unity Version Control client is not supported by this NV-AITools release.
    exit /b 3
)

if exist "%LEGACY_SKILL_TARGET%" rmdir /s /q "%LEGACY_SKILL_TARGET%"
if exist "%LEGACY_SKILL_TARGET%" (
    echo ERROR: Could not remove the obsolete skill from "%LEGACY_SKILL_TARGET%".
    exit /b 1
)

if not exist "%LOCALAPPDATA%\Programs" mkdir "%LOCALAPPDATA%\Programs"
if not exist "%APPLICATION_TARGET%" mkdir "%APPLICATION_TARGET%"
if not exist "%APPLICATION_TARGET%" (
    echo ERROR: Could not create "%APPLICATION_TARGET%".
    exit /b 1
)

if exist "%APPLICATION_TARGET%\NV-AITools.exe" (
    "%APPLICATION_TARGET%\NV-AITools.exe" broker-stop >nul 2>nul
)

copy /y "%APPLICATION_SOURCE%" "%APPLICATION_TARGET%\NV-AITools.exe" >nul
if errorlevel 1 (
    echo ERROR: Could not install NV-AITools to "%APPLICATION_TARGET%".
    exit /b 1
)

copy /y "%STARTUP_SOURCE%" "%STARTUP_TARGET%" >nul
if errorlevel 1 (
    echo ERROR: Could not install the hidden broker startup script to "%APPLICATION_TARGET%".
    exit /b 1
)

if not exist "%APPLICATION_TARGET%\config.ini" (
    copy /y "%CONFIG_SOURCE%" "%APPLICATION_TARGET%\config.ini" >nul
    if errorlevel 1 (
        echo ERROR: Could not install config.ini to "%APPLICATION_TARGET%".
        exit /b 1
    )
)

if not exist "%USERPROFILE%\.agents" mkdir "%USERPROFILE%\.agents"
if not exist "%USERPROFILE%\.agents\skills" mkdir "%USERPROFILE%\.agents\skills"
if not exist "%SKILL_TARGET%" mkdir "%SKILL_TARGET%"
if not exist "%SKILL_TARGET%" (
    echo ERROR: Could not create "%SKILL_TARGET%".
    exit /b 1
)

xcopy "%SKILL_SOURCE%\*" "%SKILL_TARGET%\" /E /I /Q /Y >nul
if errorlevel 1 (
    echo ERROR: Could not install the NV-AITools skill to "%SKILL_TARGET%".
    exit /b 1
)

reg.exe add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "NV-AITools" /t REG_SZ /d "\"%SystemRoot%\System32\wscript.exe\" //B //NoLogo \"%STARTUP_TARGET%\"" /f >nul
if errorlevel 1 (
    echo ERROR: Could not configure NV-AITools to start when this user signs in.
    exit /b 1
)

"%APPLICATION_TARGET%\NV-AITools.exe" broker-start
if errorlevel 1 (
    echo ERROR: NV-AITools was installed but its background broker did not initialize successfully.
    echo Review "%APPLICATION_TARGET%\config.ini" and "%APPLICATION_TARGET%\broker.log", then reload from the tray.
    exit /b 1
)

echo Installed NV-AITools to "%APPLICATION_TARGET%".
echo Installed the Codex skill to "%SKILL_TARGET%".
echo Edit "%APPLICATION_TARGET%\config.ini", reload it from the tray menu, then restart your AI application.
exit /b 0
