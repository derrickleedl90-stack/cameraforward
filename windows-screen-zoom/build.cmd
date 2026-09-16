@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The Windows .NET Framework C# compiler was not found.
  echo Enable or repair .NET Framework 4.8 in Windows, then try again.
  exit /b 1
)
if not exist "bin" mkdir "bin"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:app.manifest /out:bin\ScreenZoom.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ScreenZoom.cs
if errorlevel 1 exit /b 1
echo Built bin\ScreenZoom.exe
exit /b 0
