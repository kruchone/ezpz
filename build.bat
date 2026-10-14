@echo off
rem Builds ezpz.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No installs needed.
rem Usage: build.bat [output.exe]    (default: ezpz.exe)
rem Building under another name lets you rebuild while a copy of ezpz is running.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
cd /d "%~dp0"
set OUT=%~1
if "%OUT%"=="" set OUT=ezpz.exe
if not exist "%CSC%" (echo Could not find the .NET Framework C# compiler at %CSC% & exit /b 1)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:assets\ezpz.ico /out:"%OUT%" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Management.dll /r:System.Security.dll /r:Microsoft.VisualBasic.dll /r:System.Web.Extensions.dll ^
  /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" ^
  src\*.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built %OUT%
