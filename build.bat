@echo off
rem Compila o MouseBattery.exe com o compilador C# que vem com o Windows.
cd /d "%~dp0"

rem Trava: se algum .cs foi salvo com a codificacao errada (acentos viram lixo), nao compila.
powershell -NoProfile -Command "$bad = Get-ChildItem *.cs | Where-Object { [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8) -match ([string][char]0xC3 + '[\u0080-\u00BF]|' + [string][char]0xC2 + '[\u0080-\u00BF]|' + [string][char]0xE2 + [string][char]0x20AC) }; if ($bad) { Write-Host ('Codificacao quebrada em: ' + ($bad.Name -join ', ')); exit 1 }"
if errorlevel 1 exit /b 1

set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set ICON=
if exist app.ico set ICON=/win32icon:app.ico
%CSC% /nologo /target:winexe /optimize+ %ICON% /out:MouseBattery.exe MouseBattery.cs UI.cs Ajazz.cs
