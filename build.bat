@echo off
rem Compila o MouseBattery.exe usando o compilador C# que vem com o Windows.
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set ICON=
if exist app.ico set ICON=/win32icon:app.ico
%CSC% /nologo /target:winexe /optimize+ %ICON% /out:MouseBattery.exe MouseBattery.cs UI.cs
