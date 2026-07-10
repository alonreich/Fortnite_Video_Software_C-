@echo off
echo Cleaning up accidental Fortnite Video Software installation...
echo.

echo Removing registry entries...
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite Video Software" /f
reg delete "HKLM\SOFTWARE\Fortnite Video Software" /f
reg delete "HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite Video Software" /f 2>nul

echo.
echo Removing Program Files folder...
rmdir /s /q "C:\Program Files\Fortnite Video Software"

echo.
echo Removing shortcuts...
del /q "%USERPROFILE%\Desktop\Fortnite Video Software.lnk" 2>nul
del /q "%PUBLIC%\Desktop\Fortnite Video Software.lnk" 2>nul
rmdir /s /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Fortnite Video Software" 2>nul
rmdir /s /q "%PROGRAMDATA%\Microsoft\Windows\Start Menu\Programs\Fortnite Video Software" 2>nul

echo.
echo Verifying cleanup...
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fortnite Video Software" 2>nul
if exist "C:\Program Files\Fortnite Video Software" (echo WARNING: Folder still exists) else (echo SUCCESS: Folder removed)
if errorlevel 1 echo Registry key removed successfully

echo.
echo Cleanup complete.
pause