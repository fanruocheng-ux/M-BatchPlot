@echo off
setlocal
set ACAD=C:\Program Files (x86)\AutoCAD 2007
set CSC=C:\Windows\Microsoft.NET\Framework\v2.0.50727\csc.exe
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:library /platform:AnyCPU /warn:2 ^
  /out:"%~dp0bin\MyBatchPlot.dll" ^
  /r:System.dll /r:System.Data.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%ACAD%\acdbmgd.dll" /r:"%ACAD%\acmgd.dll" ^
  /r:"%~dp0lib\PdfSharp.dll" ^
  "%~dp0src\MyBatchPlot.cs"
echo EXITCODE=%ERRORLEVEL%
