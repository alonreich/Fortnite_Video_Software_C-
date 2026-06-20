DISM /Online /Cleanup-Image /RestoreHealth > %USERPROFILE%\Desktop\Repair-OS.log 2>&1
sfc /SCANNOW >> %USERPROFILE%\Desktop\Repair-OS.log 2>&1
#shutdown /s /t 0