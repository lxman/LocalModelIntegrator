@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=amd64 -no_logo
MSBuild.exe /t:Rebuild /p:Configuration=Debug "C:\Users\jorda\source\active\LocalModelIntegrator\LocalModelIntegrator.slnx"
if errorlevel 1 exit /b 1
echo --- VSIX contents ---
dir /b "C:\Users\jorda\source\active\LocalModelIntegrator\src\LocalModelIntegrator\bin\Debug\net472\*.vsix" 2>nul
if errorlevel 1 (
    echo ERROR: build succeeded but no .vsix was found under bin\Debug\net472
    exit /b 1
)
exit /b 0
