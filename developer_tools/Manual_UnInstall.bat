@echo off
:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% == 0 (
    goto :admin_granted
) else (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process cmd -ArgumentList '/c %~dpnx0' -Verb RunAs"
    exit /b
)
:admin_granted

echo Cleaning up accidental Free Video Studio installation...
echo.

echo Removing registry entries...
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio" /f 2>nul
reg delete "HKLM\SOFTWARE\Free Video Studio" /f 2>nul
reg delete "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio" /f 2>nul

echo.
echo Removing Program Files folder...
rmdir /s /q "C:\Program Files\Free Video Studio" 2>nul

echo.
echo Removing AppData and ProgramData state...
rmdir /s /q "%APPDATA%\FreeVideoStudio" 2>nul
rmdir /s /q "%PROGRAMDATA%\FreeVideoStudio" 2>nul

echo.
echo Removing shortcuts...
del /q "%USERPROFILE%\Desktop\Free Video Studio.lnk" 2>nul
del /q "%PUBLIC%\Desktop\Free Video Studio.lnk" 2>nul
rmdir /s /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Free Video Studio" 2>nul
rmdir /s /q "%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\Free Video Studio" 2>nul

echo.
echo Verifying cleanup...
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Free Video Studio" 2>nul
if exist "C:\Program Files\Free Video Studio" (echo WARNING: Folder still exists) else (echo SUCCESS: Folder removed)
if errorlevel 1 echo Registry key removed successfully

echo.
echo Cleanup complete.
pause