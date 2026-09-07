@echo off
setlocal
cd /d "%~dp0"

set CSC=
if exist "%WINDIR%\Microsoft.NET\Framework64\v3.5\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework64\v3.5\csc.exe
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v3.5\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework\v3.5\csc.exe
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not defined CSC (
  echo [错误] 未找到 C# 编译器 csc.exe，请安装 .NET Framework 3.5 或 4.x
  pause
  exit /b 1
)

set REF=%WINDIR%\Microsoft.NET\Framework64\v2.0.50727
if not exist "%REF%\mscorlib.dll" set REF=%WINDIR%\Microsoft.NET\Framework\v2.0.50727
if not exist "%REF%\mscorlib.dll" (
  echo [错误] 未找到 .NET 2.0 引用程序集目录
  pause
  exit /b 1
)

set ICON=
if exist SysMonitor.ico set ICON=/win32icon:SysMonitor.ico

"%CSC%" /nologo /target:winexe /noconfig /nostdlib+ %ICON% /out:SysMonitor.exe /reference:"%REF%\mscorlib.dll" /reference:"%REF%\System.dll" /reference:"%REF%\System.Drawing.dll" /reference:"%REF%\System.Windows.Forms.dll" /reference:"%REF%\System.Management.dll" SysMonitor.cs

if %errorlevel%==0 (
  echo 编译成功: SysMonitor.exe
) else (
  echo 编译失败
  pause
  exit /b 1
)
endlocal
