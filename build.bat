@echo off
rem Compila o MouseBattery.exe usando o compilador C# que vem com o Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:MouseBattery.exe MouseBattery.cs UI.cs
